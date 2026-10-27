using System.Diagnostics;
using System.Text.Json;

namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>音频端点/声卡的一次状态变化。</summary>
/// <param name="At">变化时刻。</param>
/// <param name="SecondsSinceStart">相对采样开始的秒数。</param>
/// <param name="Kind">变化类型：新增 / 消失 / 状态改变。</param>
/// <param name="Device">设备名。</param>
/// <param name="Detail">补充说明（变化前后的状态）。</param>
internal sealed record AudioChange(
    DateTimeOffset At,
    double SecondsSinceStart,
    string Kind,
    string Device,
    string Detail);

/// <summary>
/// 音频端点监视器：定期快照 Windows 的音频端点与声卡状态，报告变化。
/// </summary>
/// <remarks>
/// <para><b>为什么用 PnP 快照而不是 WASAPI</b>：本工具要回答的是
/// 「建立常驻 RFCOMM 连接会不会把耳机的音频链路踢掉」，
/// 端点被禁用/消失/状态转为异常就是最直接的证据。
/// 而「音频有没有卡顿」属于亚秒级问题，WASAPI 需要持续抓波形才能判定，
/// 成本与误报率都高——蓝牙链路本身就有抖动，脱离基线谈 underrun 率没有意义。
/// 因此本工具把<b>卡顿判定交给用户听感</b>，程序负责给出精确的连接/断开时间线供对账，
/// 并自动覆盖「端点消失 / 状态异常 / 设备被禁用」这类粗粒度但决定性的干扰。</para>
/// <para>实现上每轮采样起一个 PowerShell 子进程（不引入任何额外 NuGet 依赖），
/// 查询 <c>Get-PnpDevice -Class AudioEndpoint</c> 与 <c>Win32_SoundDevice</c>。</para>
/// </remarks>
internal sealed class AudioMonitor : IAsyncDisposable
{
    private static readonly string PowerShellPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private const string SnapshotCommand = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ae = @(Get-PnpDevice -Class AudioEndpoint | Select-Object FriendlyName, Status)
        $sd = @(Get-CimInstance -ClassName Win32_SoundDevice | Select-Object Name, Status)
        [pscustomobject]@{ endpoints = $ae; soundDevices = $sd } | ConvertTo-Json -Compress -Depth 4
        """;

    private readonly TimeSpan _interval;
    private readonly string _deviceFilter;
    private readonly List<AudioChange> _changes = [];
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Dictionary<string, string> _lastEndpoints = new(StringComparer.Ordinal);
    private Dictionary<string, string> _lastSoundDevices = new(StringComparer.Ordinal);
    private bool _initialized;

    /// <summary>监视器是否成功拿到过至少一次快照。</summary>
    public bool Available { get; private set; }

    /// <summary>最近一次错误信息。</summary>
    public string? LastError { get; private set; }

    public AudioMonitor(TimeSpan interval, string deviceFilter)
    {
        _interval = interval;
        _deviceFilter = deviceFilter;
    }

    /// <summary>采样期间记录到的变化。</summary>
    public IReadOnlyList<AudioChange> Changes
    {
        get
        {
            lock (_gate)
            {
                return _changes.ToList();
            }
        }
    }

    /// <summary>开始后台采样。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var snapshot = await TakeSnapshotAsync(token).ConfigureAwait(false);
                if (snapshot is null)
                {
                    continue;
                }

                Available = true;
                LastError = null;

                if (!_initialized)
                {
                    _lastEndpoints = snapshot.Endpoints;
                    _lastSoundDevices = snapshot.SoundDevices;
                    _initialized = true;
                    continue;
                }

                Diff(_lastEndpoints, snapshot.Endpoints, "音频端点", started);
                Diff(_lastSoundDevices, snapshot.SoundDevices, "声卡", started);
                _lastEndpoints = snapshot.Endpoints;
                _lastSoundDevices = snapshot.SoundDevices;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    private void Diff(
        Dictionary<string, string> before,
        Dictionary<string, string> after,
        string category,
        DateTimeOffset started)
    {
        var at = DateTimeOffset.UtcNow;
        var elapsed = Math.Round((at - started).TotalSeconds, 2);

        foreach (var (name, status) in after)
        {
            if (!before.TryGetValue(name, out var previous))
            {
                // 只报告与目标设备相关的端点，避免其它设备的日常变动刷屏。
                if (!name.Contains(_deviceFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Record(new AudioChange(at, elapsed, "新增", name, $"状态={status}"));
            }
            else if (!string.Equals(previous, status, StringComparison.Ordinal))
            {
                Record(new AudioChange(at, elapsed, "状态改变", name, $"{previous} → {status}"));
            }
        }

        foreach (var (name, status) in before)
        {
            if (!after.ContainsKey(name))
            {
                Record(new AudioChange(at, elapsed, "消失", name, $"原状态={status}"));
            }
        }
    }

    private void Record(AudioChange change)
    {
        lock (_gate)
        {
            _changes.Add(change);
        }
    }

    private async Task<AudioSnapshot?> TakeSnapshotAsync(CancellationToken token)
    {
        if (!File.Exists(PowerShellPath))
        {
            LastError = $"找不到 PowerShell：{PowerShellPath}";
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(SnapshotCommand);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            LastError = "无法启动 PowerShell";
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            LastError = "PowerShell 未返回内容";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;

            var endpoints = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("endpoints", out var endpointArray))
            {
                foreach (var item in endpointArray.EnumerateArray())
                {
                    var name = GetString(item, "FriendlyName") ?? "(未命名)";
                    var status = GetString(item, "Status") ?? "?";
                    endpoints[name] = status;
                }
            }

            var soundDevices = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("soundDevices", out var soundArray))
            {
                foreach (var item in soundArray.EnumerateArray())
                {
                    var name = GetString(item, "Name") ?? "(未命名)";
                    var status = GetString(item, "Status") ?? "?";
                    soundDevices[name] = status;
                }
            }

            return new AudioSnapshot(endpoints, soundDevices);
        }
        catch (JsonException ex)
        {
            LastError = $"解析 PowerShell 输出失败：{ex.Message}";
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常结束。
            }

            _loop = null;
        }
    }

    private sealed record AudioSnapshot(
        Dictionary<string, string> Endpoints,
        Dictionary<string, string> SoundDevices);
}
