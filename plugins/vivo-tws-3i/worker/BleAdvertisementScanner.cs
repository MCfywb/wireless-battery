using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation;

namespace VivoTws3i.Worker;

/// <summary>
/// BLE 广播扫描封装。
/// </summary>
/// <remarks>
/// 只读：不连接任何设备、不写任何数据。监听 <c>0x0837</c>（vivo Fast Pair）
/// 厂商数据段，解析由 <c>VivoTws3i.Protocol</c> 完成。
/// </remarks>
internal sealed class BleAdvertisementScanner : IDisposable
{
    private readonly ushort _companyId;
    private readonly List<byte[]> _payloads = [];
    private readonly Lock _gate = new();
    private BluetoothLEAdvertisementWatcher? _watcher;

    /// <summary>最近一次启动失败的说明；为 <c>null</c> 表示运行正常或尚未尝试。</summary>
    private string? _startError;

    public BleAdvertisementScanner(ushort companyId)
    {
        _companyId = companyId;
    }

    /// <summary>扫描是否在运行。BLE 无线电不可用等情况下可能为 <c>false</c>。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }

    /// <summary>最近一次启动失败的说明；正常时为 <c>null</c>。</summary>
    public string? StartError => _startError;

    /// <summary>开始扫描。</summary>
    /// <remarks>
    /// 非致命：若 BLE 无线电当前不可用（<see cref="System.Runtime.InteropServices.COMException"/>
    /// <c>0x800710DF</c> 等），仅记录诊断并继续，不抛异常——
    /// 因为常驻 RFCOMM 与会状态探针都不依赖 BLE，worker 仍可正常服务。
    /// </remarks>
    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        BluetoothLEAdvertisementWatcher watcher;
        try
        {
            watcher = new BluetoothLEAdvertisementWatcher();
        }
        catch (Exception ex)
        {
            _startError = $"LE 扫描器创建失败: {ex.GetType().Name}({ex.HResult:x8})";
            return;
        }

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> onReceived =
            (_, e) =>
            {
                foreach (var md in e.Advertisement.ManufacturerData)
                {
                    if (md.CompanyId != _companyId)
                    {
                        continue;
                    }

                    var raw = new byte[md.Data.Length];
                    using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(md.Data))
                    {
                        dr.ReadBytes(raw);
                    }

                    lock (_gate)
                    {
                        _payloads.Add(raw);
                    }
                }
            };

        watcher.Received += onReceived;

        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            // BLE 无线电被关闭 / 暂不可用时抛出 0x800710DF 等。
            // 不抛：worker 其余能力（常驻 RFCOMM、状态探针）不受影响。
            _startError = $"LE 扫描器启动失败: {ex.GetType().Name}({ex.HResult:x8})";
            return;
        }

        _watcher = watcher;
    }

    /// <summary>
    /// 若当前未运行且尚未释放，尝试重新启动扫描。
    /// </summary>
    /// <remarks>
    /// 用于 BLE 无线电在启动后才可用的情况：每次 poll 顺带调用，无代价
    /// （已运行时立即返回，未运行且仍失败也仅是刷新诊断）。
    /// </remarks>
    public void TryRecover()
    {
        if (_watcher is not null)
        {
            return;
        }

        Start();
    }

    /// <summary>停止扫描并释放。</summary>
    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Stop();
        }
        catch
        {
            // 蓝牙被关闭等情况下停止可能抛异常，忽略即可
        }
    }

    /// <summary>取出并清空已捕获的载荷。</summary>
    public IReadOnlyList<byte[]> Drain()
    {
        lock (_gate)
        {
            var copy = _payloads.ToArray();
            _payloads.Clear();
            return copy;
        }
    }

    public void Dispose() => Stop();
}
