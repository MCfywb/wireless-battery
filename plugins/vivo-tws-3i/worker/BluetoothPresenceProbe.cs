using VivoTws3i.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace VivoTws3i.Worker;

/// <summary>
/// 系统蓝牙连接状态探针：后台采样 + 去抖，给出稳定的连接态判定。
/// </summary>
/// <remarks>
/// <para><b>读什么</b>：<see cref="BluetoothDevice.ConnectionStatus"/>——经典蓝牙链路与本机是否还连着。
/// 这是本项目此前完全没读过的系统信号，也是唯一能补上「被动广播盲区」的证据：
/// 耳机与本机保持连接放歌时 BLE 广播会被压制，此时只有系统连接态能证明设备还在。</para>
/// <para><b>为什么必须去抖</b>：2026-10-30 两次真机实测均发现，耳机断电过程中
/// <c>ConnectionStatus</c> 会<b>瞬时回连一次</b>（一次在 6.04s 断、9.07s 又连；
/// 另一次 13.11s 断、30.26s 又连）。若采信单次瞬时翻转，会在设备其实已走时误判在线、
/// 或在设备还在时误判离线。故只有<b>连续稳定若干秒</b>才认定状态变化。</para>
/// <para><b>只读</b>：本类只查询系统已维护的状态，不发起连接、不写任何数据，
/// 与插件 <c>readOnly: true</c> 的能力声明一致。</para>
/// <para><b>绝不阻塞主请求路径</b>：WinRT 的 <c>DeviceInformation.FindAllAsync</c> /
/// <c>BluetoothDevice.FromIdAsync</c> <b>本身没有超时</b>，蓝牙栈处于异常态（无线电未就绪、
/// 正在切换、被系统挂起）时会无限挂起。因此本类所有蓝牙枚举都包了超时；
/// 且 <see cref="SetKnownMacAsync"/> 只<b>后台触发</b>解析、立即返回，
/// 调用方（Poll / ActiveRead 主线程）绝不 <c>await</c> 任何蓝牙调用——避免把整个 Worker 拖死。</para>
/// </remarks>
internal sealed class BluetoothPresenceProbe : IDisposable
{
    /// <summary>按地址解析的超时：后台触发、不阻塞主路径，给足余量即可。</summary>
    private const int MacResolveTimeoutMs = 2500;

    /// <summary>按名字兜底解析的超时：同样不阻塞主路径。</summary>
    private const int NameResolveTimeoutMs = 3000;

    private readonly string _deviceName;
    private readonly TimeSpan _sampleInterval;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly PresenceDebouncer _debouncer;

    private BluetoothDevice? _device;
    private bool _nameResolutionAttempted;

    /// <summary>最近一次原始采样值（未去抖），仅用于诊断。</summary>
    private string _lastRaw = "?";

    /// <summary>构造探针。</summary>
    /// <param name="deviceName">经典蓝牙设备名，用于无 MAC 时的兜底枚举。</param>
    /// <param name="sampleInterval">采样间隔。</param>
    /// <param name="debounce">去抖窗口：候选状态需稳定这么久才升为稳定状态。</param>
    public BluetoothPresenceProbe(string deviceName, TimeSpan sampleInterval, TimeSpan debounce)
    {
        _deviceName = deviceName;
        _sampleInterval = sampleInterval;
        _debounce = debounce;
        _debouncer = new PresenceDebouncer(debounce);
    }

    /// <summary>
    /// 去抖后的稳定连接态。<c>null</c> = 尚未判定（设备未解析到或采样失败）。
    /// </summary>
    public bool? StableConnected
    {
        get
        {
            lock (_gate)
            {
                return _debouncer.Stable;
            }
        }
    }

    /// <summary>诊断用：最近一次原始采样值文本。</summary>
    public string LastRawStatus
    {
        get
        {
            lock (_gate)
            {
                return _lastRaw;
            }
        }
    }

    /// <summary>
    /// 用已知 MAC 解析设备。<b>后台触发，立即返回</b>，绝不阻塞调用方。
    /// </summary>
    /// <remarks>
    /// <para>被动广播探明 MAC 后调用，可显著提升命中率。经验证本型号经典蓝牙地址 == BLE 地址，
    /// 故按地址枚举能稳定命中，且不依赖设备是否处于可见态（按名字枚举在配对未连接时可能返回 0）。</para>
    /// <para>本方法只把解析任务丢到后台；解析成功后由下一个采样周期（<see cref="ReadStatusAsync"/>）取用。
    /// 调用方（Poll / ActiveRead）不需要、也不应当等待它——否则蓝牙栈异常时会拖死整个 Worker。</para>
    /// </remarks>
    public Task SetKnownMacAsync(string? mac)
    {
        if (!TryParseMac(mac, out var address))
        {
            return Task.CompletedTask;
        }

        // 后台触发，不等待：即便蓝牙栈此刻挂起，主请求路径也不受影响。
        _ = ResolveByMacAsync(address);
        return Task.CompletedTask;
    }

    /// <summary>启动后台采样。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => SampleLoopAsync(token), CancellationToken.None);
    }

    private async Task SampleLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_sampleInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await SampleAsync().ConfigureAwait(false);
        }
    }

    private async Task SampleAsync()
    {
        bool? observed = await ReadStatusAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _lastRaw = observed is null ? "未知" : observed.Value ? "Connected" : "Disconnected";

            // 去抖由 PresenceDebouncer 负责（纯逻辑、可离线单测）；
            // 采样失败会原样返回上一次结论，不会翻转状态。
            _debouncer.Update(observed, DateTimeOffset.UtcNow);
        }
    }

    private async Task<bool?> ReadStatusAsync()
    {
        BluetoothDevice? device;
        lock (_gate)
        {
            device = _device;
        }

        if (device is null && !_nameResolutionAttempted)
        {
            _nameResolutionAttempted = true;
            // 尚未拿到 MAC：尝试按名字兜底解析一次（带超时，绝不无限等待）。
            device = await ResolveByNameAsync().ConfigureAwait(false);
            if (device is not null)
            {
                lock (_gate)
                {
                    _device = device;
                }
            }
        }

        if (device is null)
        {
            return null;
        }

        try
        {
            return device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取 ConnectionStatus 失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task ResolveByMacAsync(ulong address)
    {
        try
        {
            var infos = await WithTimeout(
                DeviceInformation
                    .FindAllAsync(BluetoothDevice.GetDeviceSelectorFromBluetoothAddress(address))
                    .AsTask(),
                MacResolveTimeoutMs,
                "probe:byAddress").ConfigureAwait(false);

            if (infos is null || infos.Count == 0)
            {
                return;
            }

            var resolved = await WithTimeout(
                BluetoothDevice.FromIdAsync(infos[0].Id).AsTask(),
                MacResolveTimeoutMs,
                "probe:byId").ConfigureAwait(false);

            if (resolved is not null)
            {
                lock (_gate)
                {
                    _device = resolved;
                    _nameResolutionAttempted = true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"按地址解析设备失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<BluetoothDevice?> ResolveByNameAsync()
    {
        try
        {
            var infos = await WithTimeout(
                DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector()).AsTask(),
                NameResolveTimeoutMs,
                "probe:findAll").ConfigureAwait(false);

            if (infos is null)
            {
                return null;
            }

            foreach (var info in infos)
            {
                if (!info.Name.Contains(_deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var device = await WithTimeout(
                    BluetoothDevice.FromIdAsync(info.Id).AsTask(),
                    NameResolveTimeoutMs,
                    "probe:fromId").ConfigureAwait(false);

                if (device is not null)
                {
                    return device;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"按名称解析设备失败：{ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 给 WinRT 蓝牙异步调用套一层超时。WinRT 的 <c>FindAllAsync</c> / <c>FromIdAsync</c> 自身无超时，
    /// 蓝牙栈异常时会无限挂起，故必须自行限时。
    /// </summary>
    /// <remarks>
    /// 超时后原任务仍在后台运行；用 <see cref="Task.ContinueWith(System.Action{System.Threading.Tasks.Task})"/>
    /// 观察其异常，避免成为「未观察异常」。
    /// </remarks>
    private static async Task<T?> WithTimeout<T>(Task<T> task, int ms, string label)
    {
        var delay = Task.Delay(ms);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed != task)
        {
            Console.Error.WriteLine($"[warn] 蓝牙枚举超时（{label}，{ms}ms）");
            _ = task.ContinueWith(
                t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
            return default;
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>把 <c>XX:XX:XX:XX:XX:XX</c> 解析为 48 位大端整数。</summary>
    private static bool TryParseMac(string? mac, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(mac))
        {
            return false;
        }

        var parts = mac.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
        {
            return false;
        }

        try
        {
            ulong value = 0;
            foreach (var part in parts)
            {
                value = (value << 8) | Convert.ToByte(part, 16);
            }

            address = value;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_loop is not null)
        {
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 采样任务被取消属正常退出。
            }

            _loop = null;
        }
    }
}
