using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.PluginHost;

/// <summary>Worker 通信异常。</summary>
public sealed class PluginWorkerException : Exception
{
    public PluginWorkerException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// 单个 Worker 进程的管理与 JSON Lines 通信。
/// </summary>
/// <remarks>
/// <para>实现 docs/development-plan.md §5.3：宿主写一行 JSON、从 stdout 读一行 JSON；
/// stderr 只作诊断日志；stdout 不得夹杂非 JSON 文本；单条消息最大 256 KiB。</para>
/// <para>Worker 使用固定可执行路径直接启动，<b>不</b>通过 shell，也不把设备名拼进命令行。</para>
/// </remarks>
public sealed class PluginWorkerProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly PluginHostOptions _options;
    private readonly Action<string>? _log;
    private readonly List<string> _stderrLines = [];
    private readonly object _stderrGate = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _stderrPump;
    private int _requestCounter;
    private bool _disposed;

    /// <summary>构造 Worker 进程管理器。</summary>
    /// <param name="executablePath">Worker 可执行文件的绝对路径。</param>
    /// <param name="workingDirectory">工作目录。</param>
    /// <param name="options">主机配置。</param>
    /// <param name="log">日志回调。</param>
    public PluginWorkerProcess(string executablePath, string workingDirectory, PluginHostOptions options, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(options);

        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
        _options = options;
        _log = log;
    }

    /// <summary>进程是否仍在运行。</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>最近收集到的 stderr 日志副本。</summary>
    public IReadOnlyList<string> StderrLines
    {
        get
        {
            lock (_stderrGate)
            {
                return _stderrLines.ToList();
            }
        }
    }

    /// <summary>启动 Worker 进程（若已运行则直接返回）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return;
        }

        if (!File.Exists(_executablePath))
        {
            throw new PluginWorkerException($"Worker 可执行文件不存在：{_executablePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            _process = Process.Start(startInfo)
                ?? throw new PluginWorkerException("无法启动 Worker 进程：Process.Start 返回 null");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PluginWorkerException($"启动 Worker 进程失败：{ex.Message}", ex);
        }

        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _stderrPump = Task.Run(PumpStderrAsync, CancellationToken.None);

        _log?.Invoke($"Worker 已启动：{_executablePath}（pid {_process.Id}）");

        if (_options.StartupGrace > TimeSpan.Zero)
        {
            Thread.Sleep(_options.StartupGrace);
        }
    }

    /// <summary>
    /// 发送请求并读取一行响应。
    /// </summary>
    /// <param name="method">方法名。</param>
    /// <param name="deviceId">可选设备标识。</param>
    /// <param name="timeout">超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<PluginResponse> SendAsync(
        string method,
        string? deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRunning)
        {
            throw new PluginWorkerException("Worker 进程未运行");
        }

        var requestId = $"r-{Interlocked.Increment(ref _requestCounter):D4}";
        var request = new PluginRequest
        {
            ProtocolVersion = PluginProtocol.ProtocolVersion,
            RequestId = requestId,
            Method = method,
            DeviceId = deviceId,
        };

        var payload = JsonSerializer.Serialize(request, JsonOptions);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await _stdin!.WriteLineAsync(payload.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await _stdin.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            var line = await ReadLineAsync(_stdout!, _options.MaxResponseBytes, timeoutCts.Token).ConfigureAwait(false);

            if (line is null)
            {
                throw new PluginWorkerException("Worker 进程已退出（stdout 结束）");
            }

            if (line.Length == 0)
            {
                throw new PluginWorkerException("Worker 返回空行");
            }

            PluginResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<PluginResponse>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                // stdout 夹杂非 JSON 文本属协议违规（§5.3）。
                throw new PluginWorkerException($"Worker 输出不是合法 JSON：{ex.Message}", ex);
            }

            if (response is null)
            {
                throw new PluginWorkerException("Worker 响应反序列化为 null");
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时：Worker 可能仍卡在长任务中，直接终止进程，避免流状态错乱。
            Kill();
            throw new PluginWorkerException($"Worker 请求 \"{method}\" 超时（{timeout.TotalSeconds:0.#}s）");
        }
        catch (IOException ex)
        {
            Kill();
            throw new PluginWorkerException($"与 Worker 的通信中断：{ex.Message}", ex);
        }
        catch (ObjectDisposedException ex)
        {
            Kill();
            throw new PluginWorkerException($"与 Worker 的通信中断：{ex.Message}", ex);
        }
    }

    /// <summary>终止 Worker 进程。</summary>
    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程可能已退出，忽略。
        }
    }

    /// <summary>读取一行文本，并限制最大长度，防止异常 Worker 撑爆宿主内存。</summary>
    private static async Task<string?> ReadLineAsync(StreamReader reader, int maxBytes, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new PluginWorkerException($"读取 Worker 输出失败：{ex.Message}", ex);
            }

            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    return builder.ToString();
                }

                if (c == '\r')
                {
                    continue;
                }

                builder.Append(c);

                // 上限按 UTF-8 字节估算：非 ASCII 字符最多 3 字节，留出余量。
                if (builder.Length > maxBytes / 2)
                {
                    throw new PluginWorkerException($"Worker 单条响应超过 {maxBytes} 字节上限");
                }
            }
        }
    }

    private async Task PumpStderrAsync()
    {
        var error = _process?.StandardError;
        if (error is null)
        {
            return;
        }

        try
        {
            while (await error.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrGate)
                {
                    _stderrLines.Add(line);
                    if (_stderrLines.Count > _options.MaxStderrLines)
                    {
                        _stderrLines.RemoveRange(0, _stderrLines.Count - _options.MaxStderrLines);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程退出时 stderr 流被关闭属正常。
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 忽略关闭异常。
        }

        Kill();

        if (_process is not null)
        {
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 忽略。
            }

            _process.Dispose();
            _process = null;
        }

        if (_stderrPump is not null)
        {
            try
            {
                await _stderrPump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // 忽略。
            }
        }
    }
}
