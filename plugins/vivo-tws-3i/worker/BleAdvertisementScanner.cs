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

    public BleAdvertisementScanner(ushort companyId)
    {
        _companyId = companyId;
    }

    /// <summary>开始扫描。</summary>
    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        var watcher = new BluetoothLEAdvertisementWatcher();
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
        watcher.Start();
        _watcher = watcher;
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
