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
/// </remarks>
internal sealed class BluetoothPresenceProbe : IDisposable
{
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
    /// 用已知 MAC 解析设备。被动广播探明 MAC 后调用，可显著提升命中率。
    /// </summary>
    /// <remarks>
    /// 经验证本型号经典蓝牙地址 == BLE 地址，故按地址枚举能稳定命中，
    /// 且不依赖设备是否处于可见态（按名字枚举在配对未连接时可能返回 0）。
    /// </remarks>
    public async Task SetKnownMacAsync(string? mac)
    {
        if (!TryParseMac(mac, out var address))
        {
            return;
        }

        BluetoothDevice? resolved = null;
        try
        {
            var infos = await DeviceInformation
                .FindAllAsync(BluetoothDevice.GetDeviceSelectorFromBluetoothAddress(address))
                .AsTask().ConfigureAwait(false);

            if (infos.Count > 0)
            {
                resolved = await BluetoothDevice.FromIdAsync(infos[0].Id).AsTask().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"按地址解析设备失败：{ex.GetType().Name}: {ex.Message}");
        }

        lock (_gate)
        {
            _device = resolved ?? _device;
        }
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

            Sample();
        }
    }

    private void Sample()
    {
        bool? observed = ReadStatus();

        lock (_gate)
        {
            _lastRaw = observed is null ? "未知" : observed.Value ? "Connected" : "Disconnected";

            // 去抖由 PresenceDebouncer 负责（纯逻辑、可离线单测）；
            // 采样失败会原样返回上一次结论，不会翻转状态。
            _debouncer.Update(observed, DateTimeOffset.UtcNow);
        }
    }

    private bool? ReadStatus()
    {
        BluetoothDevice? device;
        lock (_gate)
        {
            device = _device;
        }

        if (device is null && !_nameResolutionAttempted)
        {
            _nameResolutionAttempted = true;
            // 尚未拿到 MAC：尝试按名字兜底解析一次（同步等待，采样频率 1 秒，
            // 这里的 WinRT 枚举通常亚秒级返回，可接受）。
            device = ResolveByName();
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

    private BluetoothDevice? ResolveByName()
    {
        try
        {
            var infos = DeviceInformation
                .FindAllAsync(BluetoothDevice.GetDeviceSelector())
                .AsTask()
                .GetAwaiter()
                .GetResult();

            foreach (var info in infos)
            {
                if (!info.Name.Contains(_deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var device = BluetoothDevice.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
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
