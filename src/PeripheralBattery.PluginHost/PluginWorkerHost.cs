using PeripheralBattery.Core;
using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.PluginHost;

/// <summary>
/// 单个插件的运行时：管理 Worker 进程生命周期、健康状态、崩溃恢复与熔断。
/// </summary>
/// <remarks>
/// <para>实现 docs/development-plan.md §5.3 与 §6.4：</para>
/// <list type="bullet">
/// <item><description>单个插件崩溃不允许使宿主退出。</description></item>
/// <item><description>连续失败达到阈值后进入熔断状态，停止轮询，可由用户重启。</description></item>
/// <item><description>同一插件的 poll 串行化，不允许并行重入。</description></item>
/// </list>
/// </remarks>
public sealed class PluginWorkerHost : IPluginClient, IAsyncDisposable
{
    private readonly PluginDescriptor _descriptor;
    private readonly PluginHostOptions _options;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly IClock _clock;

    private PluginWorkerProcess? _process;
    private int _consecutiveFailures;
    private DateTimeOffset _nextRestartAllowedUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>构造插件运行时。</summary>
    public PluginWorkerHost(
        PluginDescriptor descriptor,
        PluginHostOptions options,
        IClock? clock = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        _descriptor = descriptor;
        _options = options;
        _clock = clock ?? SystemClock.Instance;
        _log = log;
    }

    /// <inheritdoc />
    public string PluginId => _descriptor.Manifest.Id;

    /// <summary>当前健康状态。</summary>
    public PluginHealthState Health { get; private set; } = PluginHealthState.NotStarted;

    /// <summary>连续失败次数。</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>最近一次错误描述。</summary>
    public string? LastError { get; private set; }

    /// <summary>插件描述。</summary>
    public PluginDescriptor Descriptor => _descriptor;

    /// <summary>Worker 的 stderr 日志（供诊断面板展示）。</summary>
    public IReadOnlyList<string> StderrLines => _process?.StderrLines ?? [];

    /// <inheritdoc />
    public async Task<PluginPollOutcome> PollAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(
                PluginMethods.Poll, _options.PollTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PluginPollOutcome> ActiveReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(
                PluginMethods.ActiveRead, _options.ActiveReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// 由用户触发的重启：清除熔断状态并重新拉起 Worker。
    /// </summary>
    public async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _nextRestartAllowedUtc = DateTimeOffset.MinValue;
            Health = PluginHealthState.Starting;
            LastError = null;

            await DisposeProcessAsync().ConfigureAwait(false);
            return await EnsureProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>停止 Worker（宿主退出时调用）。</summary>
    public async Task StopAsync()
    {
        await _pollGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Health = PluginHealthState.Stopped;
            await DisposeProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task<PluginPollOutcome> ReadCoreAsync(
        string method,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (Health == PluginHealthState.CircuitOpen)
        {
            return PluginPollOutcome.Failure(
                "PLUGIN_CIRCUIT_OPEN",
                $"插件 {PluginId} 连续失败 {ConsecutiveFailures} 次已熔断，需手动重启");
        }

        if (!await EnsureProcessAsync(cancellationToken).ConfigureAwait(false))
        {
            return RegisterFailure("PLUGIN_START_FAILED", LastError ?? "无法启动 Worker");
        }

        PluginResponse response;
        try
        {
            response = await _process!.SendAsync(
                method, null, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginWorkerException ex)
        {
            // Worker 崩溃或通信中断：结束进程，下次轮询按退避策略重启。
            await DisposeProcessAsync().ConfigureAwait(false);
            return RegisterFailure("PLUGIN_WORKER_FAILED", ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        var validation = PluginResponseValidator.Validate(
            response, response.RequestId, PluginId, _options, _clock.UtcNow);

        if (!validation.IsValid)
        {
            return RegisterFailure("PLUGIN_PROTOCOL_VIOLATION", validation.Error ?? "响应校验失败");
        }

        if (!response.Ok)
        {
            // 协议合法但设备不可用：属于业务失败，不算 Worker 故障，但同样计入连续失败以免无限重试。
            var code = response.Error?.Code ?? PluginErrorCodes.InternalError;
            var message = response.Error?.Message ?? "插件返回失败";
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Health = PluginHealthState.Degraded;
            LastError = $"{code}: {message}";
            return PluginPollOutcome.Failure(code, message, response.Diagnostics);
        }

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Health = PluginHealthState.Healthy;
        LastError = null;
        return new PluginPollOutcome(true, validation.Readings, response.Diagnostics);
    }

    private async Task<bool> EnsureProcessAsync(CancellationToken cancellationToken)
    {
        if (_process is { IsRunning: true })
        {
            return true;
        }

        var now = _clock.UtcNow;
        if (now < _nextRestartAllowedUtc)
        {
            return false;
        }

        Health = PluginHealthState.Starting;

        var process = new PluginWorkerProcess(
            _descriptor.WorkerExecutablePath, _descriptor.PluginRootDirectory, _options, _log);

        try
        {
            process.Start();
        }
        catch (PluginWorkerException ex)
        {
            LastError = ex.Message;
            _log?.Invoke($"插件 {PluginId} 启动失败：{ex.Message}");
            await process.DisposeAsync().ConfigureAwait(false);
            ScheduleRestartBackoff();
            return false;
        }

        _process = process;
        return true;
    }

    private void ScheduleRestartBackoff()
    {
        var failures = Math.Max(1, ConsecutiveFailures);
        var delay = TimeSpan.FromMilliseconds(Math.Min(
            _options.RestartBackoffMax.TotalMilliseconds,
            1000 * Math.Pow(2, failures - 1)));
        _nextRestartAllowedUtc = _clock.UtcNow + delay;
    }

    private PluginPollOutcome RegisterFailure(string code, string message)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        LastError = $"{code}: {message}";
        _log?.Invoke($"插件 {PluginId} 第 {failures} 次连续失败：{LastError}");

        if (failures >= _options.FailureThreshold)
        {
            Health = PluginHealthState.CircuitOpen;
            _log?.Invoke($"插件 {PluginId} 连续失败 {failures} 次，进入熔断状态，需用户重启");
        }
        else
        {
            Health = PluginHealthState.Degraded;
            ScheduleRestartBackoff();
        }

        return PluginPollOutcome.Failure(code, message);
    }

    private async Task DisposeProcessAsync()
    {
        if (_process is null)
        {
            return;
        }

        var process = _process;
        _process = null;
        await process.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeProcessAsync().ConfigureAwait(false);
        _pollGate.Dispose();
    }
}
