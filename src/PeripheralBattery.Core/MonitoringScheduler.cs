using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.Core;

/// <summary>状态变化事件。</summary>
public sealed class StateChangedEventArgs : EventArgs
{
    public StateChangedEventArgs(bool hasChanges) => HasChanges = hasChanges;

    /// <summary>本次是否包含实质数值变化（false 表示仅过期降级或无变化）。</summary>
    public bool HasChanges { get; }
}

/// <summary>
/// 轮询调度器：按固定间隔驱动各插件读取，写入 <see cref="DeviceStateStore"/>，并广播状态变化。
/// </summary>
/// <remarks>
/// <para>实现 docs/development-plan.md §6.4：设备 IO 全部在后台执行，不阻塞 UI 线程；
/// 同一插件的 poll 串行化，不允许并行重入（每插件一把 <see cref="SemaphoreSlim"/>）。</para>
/// <para>单个插件失败（超时、崩溃、返回错误）不影响其他插件继续轮询。</para>
/// </remarks>
public sealed class MonitoringScheduler : IAsyncDisposable
{
    private readonly List<IPluginClient> _clients;
    private readonly DeviceStateStore _store;
    private readonly TimeSpan _interval;
    private readonly IClock _clock;
    private readonly Action<string>? _log;

    private readonly List<SemaphoreSlim> _pluginLocks;
    private readonly List<Task> _loops = [];

    private CancellationTokenSource? _cts;
    private Task? _expiryLoop;

    /// <summary>构造调度器。</summary>
    public MonitoringScheduler(
        IEnumerable<IPluginClient> clients,
        DeviceStateStore store,
        TimeSpan interval,
        IClock? clock = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _clients = [.. clients];
        _store = store;
        _interval = interval;
        _clock = clock ?? SystemClock.Instance;
        _log = log;
        _pluginLocks = [.. _clients.Select(_ => new SemaphoreSlim(1, 1))];
    }

    /// <summary>状态变化事件；UI 应据此重建托盘菜单。</summary>
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>设备状态存储（只读访问请用 <see cref="DeviceStateStore.GetDevices"/>）。</summary>
    public DeviceStateStore Store => _store;

    /// <summary>启动轮询。</summary>
    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        for (var i = 0; i < _clients.Count; i++)
        {
            var index = i;
            _loops.Add(Task.Run(() => PluginLoopAsync(index, token), CancellationToken.None));
        }

        _expiryLoop = Task.Run(() => ExpiryLoopAsync(token), CancellationToken.None);
    }

    /// <summary>停止轮询并等待收尾。</summary>
    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
            if (_expiryLoop is not null)
            {
                await _expiryLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loops.Clear();
            _expiryLoop = null;
        }
    }

    /// <summary>立即触发一次全部插件轮询（托盘「立即刷新」）。</summary>
    public async Task RefreshNowAsync()
    {
        var tasks = _clients.Select((_, i) => PollOnceAsync(i, CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PluginLoopAsync(int index, CancellationToken token)
    {
        var client = _clients[index];
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(index, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 单个插件的异常不得冒泡终止宿主（§5.3）。
                _log?.Invoke($"插件 {client.PluginId} 轮询异常：{ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(int index, CancellationToken token)
    {
        var client = _clients[index];
        var gate = _pluginLocks[index];

        // 同一插件串行化：不允许 poll 并行重入（§6.4）。
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var outcome = await client.PollAsync(token).ConfigureAwait(false);
            var now = _clock.UtcNow;

            if (!outcome.Ok)
            {
                _log?.Invoke($"插件 {client.PluginId} 读取失败：{outcome.ErrorCode} {outcome.ErrorMessage}");
                foreach (var reading in outcome.Readings)
                {
                    _store.Apply([reading], now);
                }

                if (outcome.ErrorCode is { } code)
                {
                    foreach (var reading in outcome.Readings)
                    {
                        _store.RecordDeviceError(reading.DeviceId, code);
                    }
                }

                RaiseChanged(hasChanges: false);
                return;
            }

            var changed = _store.Apply(outcome.Readings, now);
            foreach (var diagnostic in outcome.Diagnostics)
            {
                _store.AddDiagnostic($"[{client.PluginId}] {diagnostic}");
            }

            RaiseChanged(changed);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExpiryLoopAsync(CancellationToken token)
    {
        // 过期降级检查频率取轮询间隔的一半，保证 UI 能较快看到"已过期"。
        var tick = TimeSpan.FromMilliseconds(Math.Max(1000, _interval.TotalMilliseconds / 2));
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tick, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_store.Expire(_clock.UtcNow))
            {
                RaiseChanged(hasChanges: false);
            }
        }
    }

    private void RaiseChanged(bool hasChanges)
        => StateChanged?.Invoke(this, new StateChangedEventArgs(hasChanges));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var gate in _pluginLocks)
        {
            gate.Dispose();
        }
    }
}
