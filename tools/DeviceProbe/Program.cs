// Phase 0 探针：BLE GATT 拓扑枚举、经典蓝牙 SDP 服务发现，以及（经批准的）RFCOMM 电量查询。
using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

var targetName = args.Length > 0 ? args[0] : "vivo TWS 3i";
var readValues = !args.Contains("--no-read");

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine($"=== PeripheralBattery DeviceProbe (只读) ===");
Console.WriteLine($"目标设备名: {targetName}");
Console.WriteLine($"读取特征值: {readValues}");
Console.WriteLine();

// 经典蓝牙（BR/EDR）SDP 服务发现：只查询服务记录，不连接 socket、不发送任何数据。
if (args.Contains("--rfcomm"))
{
    return await ProbeRfcommServicesAsync(targetName);
}

// 已获用户批准的最小写操作集：连接私有 RFCOMM → 握手 0x0300 → 电量查询 0x0207。
// 只发查询类命令，不发送任何设置 / 配对 / 固件 / 模式切换命令。
if (args.Contains("--rfcomm-probe"))
{
    return await ProbeRfcommBatteryAsync(targetName);
}

static string FormatUuid(Guid uuid)
{
    var s = uuid.ToString().ToLowerInvariant();
    if (s.EndsWith("-0000-1000-8000-00805f9b34fb"))
    {
        var shortId = s.Substring(4, 4);
        return $"0x{shortId} (SIG base: {s})";
    }
    return s;
}

var selector = BluetoothLEDevice.GetDeviceSelectorFromDeviceName(targetName);
DeviceInformationCollection infos;
try
{
    infos = await DeviceInformation.FindAllAsync(selector);
}
catch (Exception ex)
{
    Console.WriteLine($"设备枚举失败: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

Console.WriteLine($"匹配到 {infos.Count} 个 BLE 设备接口");
if (infos.Count == 0)
{
    Console.WriteLine("提示：设备需已配对且蓝牙开启；若计数为 0 不代表设备不支持，只代表当前不可见。");
    return 2;
}

foreach (var info in infos)
{
    Console.WriteLine();
    Console.WriteLine($"===== {info.Name} =====");
    Console.WriteLine($"Id: {info.Id}");
    Console.WriteLine($"IsEnabled: {info.IsEnabled}  IsDefault: {info.IsDefault}");

    BluetoothLEDevice? dev = null;
    try
    {
        dev = await BluetoothLEDevice.FromIdAsync(info.Id);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FromIdAsync 异常: {ex.GetType().Name}: {ex.Message}");
    }

    if (dev is null)
    {
        Console.WriteLine("FromIdAsync 返回 null，无法继续。");
        continue;
    }

    Console.WriteLine($"BluetoothAddress: 0x{dev.BluetoothAddress:X12}");
    Console.WriteLine($"ConnectionStatus: {dev.ConnectionStatus}");
    Console.WriteLine($"DeviceAccessInformation.CurrentStatus: {dev.DeviceAccessInformation.CurrentStatus}");

    GattDeviceServicesResult svcResult;
    try
    {
        svcResult = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"GetGattServicesAsync 异常: {ex.GetType().Name}: {ex.Message}");
        dev.Dispose();
        continue;
    }

    Console.WriteLine($"服务发现结果: {svcResult.Status}（协议错误 0x{svcResult.ProtocolError?.ToString("X") ?? "-"}），服务数 {svcResult.Services.Count}");
    if (svcResult.Status != GattCommunicationStatus.Success)
    {
        dev.Dispose();
        continue;
    }

    foreach (var svc in svcResult.Services)
    {
        Console.WriteLine();
        Console.WriteLine($"  [Service] {FormatUuid(svc.Uuid)}");

        GattCharacteristicsResult chResult;
        try
        {
            chResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    GetCharacteristicsAsync 异常: {ex.GetType().Name}: {ex.Message}");
            continue;
        }

        if (chResult.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"    GetCharacteristicsAsync 失败: {chResult.Status}");
            continue;
        }

        foreach (var ch in chResult.Characteristics)
        {
            Console.WriteLine($"    [Char] {FormatUuid(ch.Uuid)}");
            Console.WriteLine($"           props={ch.CharacteristicProperties} handle={ch.AttributeHandle}");

            try
            {
                var descResult = await ch.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
                if (descResult.Status == GattCommunicationStatus.Success)
                {
                    foreach (var d in descResult.Descriptors)
                    {
                        var extra = string.Empty;
                        if (d.Uuid == GattDescriptorUuids.CharacteristicUserDescription)
                        {
                            try
                            {
                                var vr = await d.ReadValueAsync(BluetoothCacheMode.Uncached);
                                if (vr.Status == GattCommunicationStatus.Success && vr.Value.Length > 0)
                                {
                                    using var dr = DataReader.FromBuffer(vr.Value);
                                    extra = $" = \"{dr.ReadString(vr.Value.Length)}\"";
                                }
                            }
                            catch
                            {
                                // 描述符读取失败不影响主流程
                            }
                        }
                        Console.WriteLine($"           [Desc] {FormatUuid(d.Uuid)}{extra}");
                    }
                }
            }
            catch
            {
                // 部分设备枚举描述符会抛异常，忽略
            }

            if (!readValues)
            {
                continue;
            }

            if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
            {
                try
                {
                    var r = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (r.Status == GattCommunicationStatus.Success)
                    {
                        var buf = new byte[r.Value.Length];
                        using (var dr = DataReader.FromBuffer(r.Value))
                        {
                            dr.ReadBytes(buf);
                        }
                        Console.WriteLine($"           READ({buf.Length} bytes): {Convert.ToHexString(buf)}");
                    }
                    else
                    {
                        Console.WriteLine($"           READ 失败: {r.Status}（协议错误 0x{r.ProtocolError?.ToString("X") ?? "-"}）");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"           READ 异常: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    dev.Dispose();
}

Console.WriteLine();
Console.WriteLine("=== 探针结束（仅只读操作） ===");
return 0;

// 最小写操作集（2026-10-26 用户批准）：连接私有 RFCOMM → 握手 0x0300 → 电量查询 0x0207。
// 只发查询类命令；不发送设置 / 配对 / 固件 / 模式切换命令。
// 帧格式与命令码依据上游对 vivo TWS 3e / 家族的协议记录，本型号未验证，只作探测假设。
static async Task<int> ProbeRfcommBatteryAsync(string targetName)
{
    const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

    // FF ver flags len vendor(2) cmd(2) payload
    byte[] handshake = { 0xFF, 0x04, 0x00, 0x00, 0x00, 0x0A, 0x03, 0x00 };   // 0x0300 ver4 vendor GAIA 0x000A
    byte[] batteryQuery = { 0xFF, 0x04, 0x00, 0x00, 0x00, 0x1B, 0x02, 0x07 }; // 0x0207 ver4 vendor vivo 0x001B

    var selector = BluetoothDevice.GetDeviceSelectorFromDeviceName(targetName);
    var infos = await DeviceInformation.FindAllAsync(selector);
    if (infos.Count == 0)
    {
        Console.WriteLine("未匹配到经典蓝牙设备，放弃。");
        return 2;
    }

    var dev = await BluetoothDevice.FromIdAsync(infos[0].Id);
    if (dev is null)
    {
        Console.WriteLine("FromIdAsync 返回 null。");
        return 2;
    }

    Console.WriteLine($"经典蓝牙 ConnectionStatus: {dev.ConnectionStatus}");
    var svcId = RfcommServiceId.FromUuid(Guid.Parse(VivoPrivateUuid));
    var r = await dev.GetRfcommServicesForIdAsync(svcId, BluetoothCacheMode.Uncached);
    Console.WriteLine($"定向 SDP 命中数: {r.Services.Count}  error={r.Error}");
    if (r.Services.Count == 0)
    {
        Console.WriteLine("SDP 未命中私有通道，放弃连接。");
        return 3;
    }

    var svc = r.Services[0];
    Console.WriteLine($"目标通道: host={svc.ConnectionHostName}  service={svc.ConnectionServiceName}");

    var socket = new StreamSocket();
    try
    {
        await socket.ConnectAsync(
            svc.ConnectionHostName,
            svc.ConnectionServiceName,
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
        Console.WriteLine("RFCOMM 连接成功");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"RFCOMM 连接失败: {ex.GetType().Name}: {ex.Message} (HResult 0x{ex.HResult:X8})");
        socket.Dispose();
        return 4;
    }

    // 复用同一个 DataWriter：反复 new + DetachStream 会让底层流被释放，第二次写入即抛 ObjectDisposedException。
    var writer = new DataWriter(socket.OutputStream);

    Console.WriteLine();
    Console.WriteLine($"--- 发送握手 0x0300: {Convert.ToHexString(handshake)} ---");
    await SendAsync(writer, handshake);
    ReportReceived("握手响应", await CollectAsync(socket, 3000));

    Console.WriteLine();
    Console.WriteLine($"--- 发送电量查询 0x0207: {Convert.ToHexString(batteryQuery)} ---");
    await SendAsync(writer, batteryQuery);
    ReportReceived("电量响应", await CollectAsync(socket, 3000));

    writer.DetachStream();

    socket.Dispose();
    Console.WriteLine();
    Console.WriteLine("=== RFCOMM 探测结束（仅发送握手与电量查询两类命令） ===");
    return 0;
}

static async Task SendAsync(DataWriter writer, byte[] frame)
{
    writer.WriteBytes(frame);
    await writer.StoreAsync();
    await writer.FlushAsync();
}

// 在给定时间窗内收集所有入站字节；超时即返回已收到的部分（可能为空 = 无响应）。
static async Task<List<byte>> CollectAsync(StreamSocket socket, int ms)
{
    var all = new List<byte>();
    var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
    var sw = Stopwatch.StartNew();
    try
    {
        while (sw.ElapsedMilliseconds < ms)
        {
            var remaining = (int)Math.Max(1, ms - sw.ElapsedMilliseconds);

            // 不能用 CancellationToken 取消 LoadAsync：取消会连带释放底层流，后续写入即抛 ObjectDisposedException。
            var loadTask = reader.LoadAsync(256).AsTask();
            var completed = await Task.WhenAny(loadTask, Task.Delay(remaining));
            if (completed != loadTask)
            {
                break;
            }

            var n = await loadTask;
            if (n == 0)
            {
                break;
            }

            var buf = new byte[n];
            reader.ReadBytes(buf);
            all.AddRange(buf);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[接收异常] {ex.GetType().Name}: {ex.Message} (HResult 0x{ex.HResult:X8})");
    }

    return all;
}

// 打印原始字节，并按上游帧格式尝试解析（解析结论标注为「未验证假设」）。
static void ReportReceived(string label, List<byte> data)
{
    Console.WriteLine($"[{label}] 收到 {data.Count} 字节" + (data.Count > 0 ? $": {Convert.ToHexString(data.ToArray())}" : "（无响应）"));
    if (data.Count == 0)
    {
        Console.WriteLine("  提示：无响应 ≠ 该型号不支持，可能是版本/vendor 用错、通道退化或需先注册通知链。");
        return;
    }

    var i = 0;
    while (i + 8 <= data.Count)
    {
        if (data[i] != 0xFF)
        {
            i++;
            continue;
        }

        var ver = data[i + 1];
        var flags = data[i + 2];
        var len = data[i + 3];
        var vendor = (data[i + 4] << 8) | data[i + 5];
        var cmd = (data[i + 6] << 8) | data[i + 7];
        var payloadStart = i + 8;

        if (payloadStart + len > data.Count)
        {
            Console.WriteLine($"  [截断帧] cmd=0x{cmd:X4} 声明 payload {len} 字节，实到 {data.Count - payloadStart} 字节");
            break;
        }

        var payload = data.GetRange(payloadStart, len).ToArray();
        Console.WriteLine($"  [帧] ver={ver} flags=0x{flags:X2} len={len} vendor=0x{vendor:X4} cmd=0x{cmd:X4} payload={Convert.ToHexString(payload)}");

        if (cmd == 0x8300)
        {
            Console.WriteLine("       → 握手响应 0x8300");
        }
        else if (cmd == 0x8207 && payload.Length >= 5)
        {
            Console.WriteLine($"       → 电量 0x8207【按上游假设解析，本型号未验证】左={payload[1]} 右={payload[2]} 仓={payload[3]} 充电位=0x{payload[4]:X2}");
            Console.WriteLine("         位含义（假设）：bit0=左充电 bit1=右充电 bit2=仓充电；数值 >100 或 0xFF = 该部件未知/无效，不得显示为 0");
        }

        i = payloadStart + len;
    }
}

// 逐个属性取值：WinRT 对系统占用的服务项常在某些属性上抛 COMException，逐项降级可保留其余信息。
static string? SafeGet(Func<string> getter, out string? error)
{
    try
    {
        error = null;
        return getter();
    }
    catch (Exception ex)
    {
        error = $"{ex.GetType().Name}";
        return null;
    }
}

// 经典蓝牙 SDP：列出设备声明的全部 RFCOMM 服务，并定向查询 vivo 私有 UUID。
static async Task<int> ProbeRfcommServicesAsync(string targetName)
{
    const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

    var selector = BluetoothDevice.GetDeviceSelectorFromDeviceName(targetName);
    DeviceInformationCollection infos;
    try
    {
        infos = await DeviceInformation.FindAllAsync(selector);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"经典蓝牙设备枚举失败: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"匹配到 {infos.Count} 个经典蓝牙设备接口");
    if (infos.Count == 0)
    {
        Console.WriteLine("提示：设备需已配对且处于连接状态；计数为 0 不代表不支持。");
        return 2;
    }

    foreach (var info in infos)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {info.Name} =====");
        Console.WriteLine($"Id: {info.Id}");

        BluetoothDevice? dev = null;
        try
        {
            dev = await BluetoothDevice.FromIdAsync(info.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FromIdAsync 异常: {ex.GetType().Name}: {ex.Message}");
        }

        if (dev is null)
        {
            Console.WriteLine("FromIdAsync 返回 null，无法继续。");
            continue;
        }

        Console.WriteLine($"HostName: {dev.HostName}  ConnectionStatus: {dev.ConnectionStatus}");
        Console.WriteLine($"ClassOfDevice: {dev.ClassOfDevice}");

        try
        {
            var all = await dev.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
            Console.WriteLine($"全量 SDP 查询: 服务数 {all.Services.Count}  error={all.Error}");
            for (var i = 0; i < all.Services.Count; i++)
            {
                try
                {
                    var s = all.Services[i];
                    var uuid = SafeGet(() => s.ServiceId.Uuid.ToString(), out _);
                    var shortId = SafeGet(() => $"0x{s.ServiceId.AsShortId():X}", out _);
                    var protection = SafeGet(() => s.ProtectionLevel.ToString(), out var perr) ?? $"<{perr}>";
                    Console.WriteLine($"  [RFCOMM] uuid={uuid}  shortId={shortId}  protection={protection}");
                }
                catch (Exception exItem)
                {
                    Console.WriteLine($"  [RFCOMM #{i}] 读取该项失败: {exItem.GetType().Name}: {exItem.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetRfcommServicesAsync 异常: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var target = RfcommServiceId.FromUuid(Guid.Parse(VivoPrivateUuid));
            var hit = await dev.GetRfcommServicesForIdAsync(target, BluetoothCacheMode.Uncached);
            Console.WriteLine($"定向查询 {VivoPrivateUuid}: 命中数 {hit.Services.Count}  error={hit.Error}");
            foreach (var s in hit.Services)
            {
                Console.WriteLine($"  [HIT] shortId=0x{s.ServiceId.AsShortId():X}  protection={s.ProtectionLevel}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"定向 SDP 查询异常: {ex.GetType().Name}: {ex.Message}");
        }

        dev.Dispose();
    }

    Console.WriteLine();
    Console.WriteLine("=== SDP 查询结束（未连接 socket、未发送任何数据） ===");
    return 0;
}
