// Phase 0 探针：BLE GATT 拓扑枚举、经典蓝牙 SDP 服务发现，以及（经批准的）RFCOMM 电量查询。
using System.Diagnostics;
using System.Text;
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

// 用户批准的变体矩阵：握手后用不同 ver / vendor 组合查询 0x0207。
if (args.Contains("--rfcomm-matrix"))
{
    return await ProbeRfcommMatrixAsync(targetName);
}

// E2：注册通知链（v4 与 v3 各一轮）→ 长监听，观察 0x82xx ACK 与 0x8207。
if (args.Contains("--rfcomm-register"))
{
    var registerListenMs = 30000;
    var registerIdx = Array.IndexOf(args, "--listen-ms");
    if (registerIdx >= 0 && registerIdx + 1 < args.Length && int.TryParse(args[registerIdx + 1], out var registerParsed))
    {
        registerListenMs = registerParsed;
    }

    return await ProbeRfcommRegisterAsync(targetName, registerListenMs);
}

// E5 → E1 → E3：被动交互监听（含 0x8509 应答）→ V3 握手 → V3 电量查询。
if (args.Contains("--rfcomm-v3"))
{
    return await ProbeRfcommV3Async(targetName);
}

// 纯监听：连接后不发送任何命令，只观察设备是否自发上报（判断埋点是主动推送还是查询触发）。
if (args.Contains("--rfcomm-listen"))
{
    var ms = 8000;
    var idx = Array.IndexOf(args, "--listen-ms");
    if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var parsed))
    {
        ms = parsed;
    }

    return await ProbeRfcommListenAsync(targetName, ms);
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

    var socket = await ConnectPrivateChannelAsync(dev, VivoPrivateUuid);
    if (socket is null)
    {
        return 3;
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

// 定向 SDP 查询私有通道并建立 RFCOMM 连接；失败时打印原因并返回 null。
static async Task<StreamSocket?> ConnectPrivateChannelAsync(BluetoothDevice dev, string uuid)
{
    Console.WriteLine($"经典蓝牙 ConnectionStatus: {dev.ConnectionStatus}");
    var svcId = RfcommServiceId.FromUuid(Guid.Parse(uuid));
    var r = await dev.GetRfcommServicesForIdAsync(svcId, BluetoothCacheMode.Uncached);
    Console.WriteLine($"定向 SDP 命中数: {r.Services.Count}  error={r.Error}");
    if (r.Services.Count == 0)
    {
        Console.WriteLine("SDP 未命中私有通道，放弃连接。");
        return null;
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
        return socket;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"RFCOMM 连接失败: {ex.GetType().Name}: {ex.Message} (HResult 0x{ex.HResult:X8})");
        socket.Dispose();
        return null;
    }
}

// 用户批准的变体矩阵（2026-10-26）：握手后依次用不同 ver / vendor 组合查询 0x0207，逐条记录原始响应。
// 目的：握手响应为 ver=3，与上游「握手 v4 / 电量恒 v4」不一致，需定位本型号实际接受的帧。
static async Task<int> ProbeRfcommMatrixAsync(string targetName)
{
    const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";
    byte[] handshake = { 0xFF, 0x04, 0x00, 0x00, 0x00, 0x0A, 0x03, 0x00 };

    var variants = new (string Label, byte[] Frame)[]
    {
        ("ver=3 vendor=0x001B(vivo)", new byte[] { 0xFF, 0x03, 0x00, 0x00, 0x00, 0x1B, 0x02, 0x07 }),
        ("ver=3 vendor=0x000A(GAIA)", new byte[] { 0xFF, 0x03, 0x00, 0x00, 0x00, 0x0A, 0x02, 0x07 }),
        ("ver=4 vendor=0x000A(GAIA)", new byte[] { 0xFF, 0x04, 0x00, 0x00, 0x00, 0x0A, 0x02, 0x07 }),
        ("ver=4 vendor=0x001B(vivo)", new byte[] { 0xFF, 0x04, 0x00, 0x00, 0x00, 0x1B, 0x02, 0x07 }),
    };

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

    var socket = await ConnectPrivateChannelAsync(dev, VivoPrivateUuid);
    if (socket is null)
    {
        return 3;
    }

    var writer = new DataWriter(socket.OutputStream);

    Console.WriteLine();
    Console.WriteLine($"--- 发送握手 0x0300: {Convert.ToHexString(handshake)} ---");
    await SendAsync(writer, handshake);
    ReportReceived("握手响应", await CollectAsync(socket, 3000));

    foreach (var (label, frame) in variants)
    {
        Console.WriteLine();
        Console.WriteLine($"--- 电量查询 0x0207 [{label}]: {Convert.ToHexString(frame)} ---");
        await SendAsync(writer, frame);
        ReportReceived($"电量响应 [{label}]", await CollectAsync(socket, 3000));
    }

    writer.DetachStream();
    socket.Dispose();
    Console.WriteLine();
    Console.WriteLine("=== 变体矩阵结束（全程只发送握手与电量查询两类命令） ===");
    return 0;
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
// GAIA 帧的最小表示，供解析与打印共用。
// 按 GAIA 帧格式切分字节流。flags 的 0x02 位表示长度域扩为 2 字节——不处理会整体错位。
static List<GFrame> ParseFrames(List<byte> data)
{
    var frames = new List<GFrame>();
    var i = 0;
    while (i + 4 <= data.Count)
    {
        if (data[i] != 0xFF)
        {
            i++;
            continue;
        }

        var ver = data[i + 1];
        var flags = data[i + 2];
        int len;
        int vIdx;
        if ((flags & 0x02) != 0)
        {
            if (i + 5 > data.Count)
            {
                break;
            }

            len = (data[i + 3] << 8) | data[i + 4];
            vIdx = i + 5;
        }
        else
        {
            len = data[i + 3];
            vIdx = i + 4;
        }

        if (vIdx + 4 > data.Count)
        {
            break;
        }

        var vendor = (data[vIdx] << 8) | data[vIdx + 1];
        var cmd = (data[vIdx + 2] << 8) | data[vIdx + 3];
        var start = vIdx + 4;
        if (start + len > data.Count)
        {
            break;
        }

        frames.Add(new GFrame(ver, flags, vendor, cmd, data.GetRange(start, len).ToArray()));
        i = start + len;
    }

    return frames;
}

// 构造 GAIA 帧（与上游 frame() 对齐）。
static byte[] BuildFrame(int version, int vendor, int command, byte[] payload, int flags = 0)
{
    var extended = (flags & 0x02) != 0;
    var headerSize = extended ? 5 : 4;
    var buf = new byte[headerSize + 4 + payload.Length];
    buf[0] = 0xFF;
    buf[1] = (byte)version;
    buf[2] = (byte)flags;
    if (extended)
    {
        buf[3] = (byte)((payload.Length >> 8) & 0xFF);
        buf[4] = (byte)(payload.Length & 0xFF);
    }
    else
    {
        buf[3] = (byte)payload.Length;
    }

    var v = headerSize;
    buf[v] = (byte)((vendor >> 8) & 0xFF);
    buf[v + 1] = (byte)(vendor & 0xFF);
    buf[v + 2] = (byte)((command >> 8) & 0xFF);
    buf[v + 3] = (byte)(command & 0xFF);
    Array.Copy(payload, 0, buf, v + 4, payload.Length);
    return buf;
}

// 应答耳机的 0x8509 时间请求：0x0509，payload 7 字节纯二进制（非 BCD），完整年份 = 世纪*100 + 年。
// 上游 probe 注明「必须应答，否则它可能不继续交互」。
static byte[] BuildTimeResponse()
{
    var now = DateTime.Now;
    var payload = new byte[]
    {
        (byte)((now.Year / 100) & 0xFF),
        (byte)(now.Year % 100),
        (byte)now.Month,
        (byte)now.Day,
        (byte)now.Hour,
        (byte)now.Minute,
        (byte)now.Second,
    };
    return BuildFrame(3, 0x001B, 0x0509, payload);
}

static void ReportReceived(string label, List<byte> data)
{
    Console.WriteLine($"[{label}] 收到 {data.Count} 字节" + (data.Count > 0 ? $": {Convert.ToHexString(data.ToArray())}" : "（无响应）"));
    if (data.Count == 0)
    {
        Console.WriteLine("  提示：0x8207 是耳机主动推送的 report，不是查询的响应；空窗口只说明这段时间内没推送，不等于不支持。");
        return;
    }

    foreach (var fr in ParseFrames(data))
    {
        Console.WriteLine($"  [帧] ver={fr.Version} flags=0x{fr.Flags:X2} len={fr.Payload.Length} vendor=0x{fr.Vendor:X4} cmd=0x{fr.Command:X4}");

        if (fr.Command == 0x8300)
        {
            Console.WriteLine($"       → 握手响应 0x8300，payload={Convert.ToHexString(fr.Payload)}");
        }
        else if (fr.Command == 0x8207 && fr.Payload.Length >= 5)
        {
            Console.WriteLine($"       → 电量 0x8207【按上游假设解析，本型号未验证】左={fr.Payload[1]} 右={fr.Payload[2]} 仓={fr.Payload[3]} 充电位=0x{fr.Payload[4]:X2}");
            Console.WriteLine("         位含义（假设）：bit0=左充电 bit1=右充电 bit2=仓充电；数值 >100 或 0xFF = 该部件未知/无效，不得显示为 0");
        }
        else if (fr.Command == 0x8509)
        {
            Console.WriteLine($"       → 耳机索要时间 0x8509，payload={Convert.ToHexString(fr.Payload)}（已自动应答）");
        }
        else if (fr.Command == 0x8224)
        {
            ReportTelemetry(fr.Payload);
        }
        else
        {
            Console.WriteLine($"       payload={Convert.ToHexString(fr.Payload)}");
        }
    }
}

// 埋点上报 0x8224：payload 是 JSON，其中的 C 字段按上游记录为左右耳电量，可用于交叉验证 0x8207。
static void ReportTelemetry(byte[] payload)
{
    // 埋点 payload 带 1~2 字节前导（如 00 01），JSON 从第一个 '{' 开始。
    var jsonStart = Array.IndexOf(payload, (byte)'{');
    if (jsonStart < 0)
    {
        Console.WriteLine($"       埋点 0x8224 payload={Convert.ToHexString(payload)}");
        return;
    }

    var json = Encoding.UTF8.GetString(payload, jsonStart, payload.Length - jsonStart);
    var preview = json.Length > 260 ? json.Substring(0, 260) + "…" : json;
    Console.WriteLine($"       → 埋点 0x8224 JSON: {preview}");
    Console.WriteLine($"         提取【按上游记录，未与官方 App 交叉验证】C(左右耳电量)={ExtractJson(json, "C")}  B={ExtractJson(json, "B")}  P={ExtractJson(json, "P")}  V(固件)={ExtractJson(json, "V")}");
}

static string ExtractJson(string json, string key)
{
    var marker = $"\"{key}\":\"";
    var start = json.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0)
    {
        return "-";
    }

    start += marker.Length;
    var end = json.IndexOf('"', start);
    return end < 0 ? "-" : json.Substring(start, end - start);
}

// 纯监听：建立 RFCOMM 连接后不发任何命令，只收集设备自发上报的帧。
static async Task<int> ProbeRfcommListenAsync(string targetName, int ms)
{
    const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

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

    var socket = await ConnectPrivateChannelAsync(dev, VivoPrivateUuid);
    if (socket is null)
    {
        return 3;
    }

    Console.WriteLine();
    Console.WriteLine($"--- 不发送任何命令，仅监听 {ms} ms ---");
    ReportReceived($"被动监听 {ms}ms", await CollectAsync(socket, ms));

    socket.Dispose();
    Console.WriteLine();
    Console.WriteLine("=== 监听结束（全程未发送任何命令） ===");
    return 0;
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

// 交互式收集：持续读取给定时间窗；遇到耳机的 0x8509 时间请求立即应答（上游 probe 要求）。
static async Task<List<byte>> CollectInteractiveAsync(StreamSocket socket, DataWriter writer, int ms)
{
    var all = new List<byte>();
    var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
    var sw = Stopwatch.StartNew();
    var respondedUpTo = 0;

    while (sw.ElapsedMilliseconds < ms)
    {
        var remaining = (int)Math.Max(1, ms - sw.ElapsedMilliseconds);

        // 不取消 LoadAsync：取消会连带释放 socket 流。
        var loadTask = reader.LoadAsync(256).AsTask();
        var completed = await Task.WhenAny(loadTask, Task.Delay(remaining));
        if (completed != loadTask)
        {
            break;
        }

        uint n;
        try
        {
            n = await loadTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[接收异常] {ex.GetType().Name}: {ex.Message}");
            break;
        }

        if (n == 0)
        {
            break;
        }

        var buf = new byte[n];
        reader.ReadBytes(buf);
        all.AddRange(buf);

        // 只对本次新增的帧做应答判定，避免重复应答同一帧。
        var frames = ParseFrames(all);
        for (var k = respondedUpTo; k < frames.Count; k++)
        {
            var fr = frames[k];
            if (fr.Vendor == 0x001B && fr.Command == 0x8509)
            {
                var reply = BuildTimeResponse();
                Console.WriteLine($"[自动应答] 收到 0x8509 时间请求 → 发送 0x0509 {Convert.ToHexString(reply)}");
                await SendAsync(writer, reply);
            }
        }

        respondedUpTo = frames.Count;
    }

    return all;
}

// E5 → E1 → E3：先被动交互监听，再 V3 握手，再 V3 电量查询。
static async Task<int> ProbeRfcommV3Async(string targetName)
{
    const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

    var selector = BluetoothDevice.GetDeviceSelectorFromDeviceName(targetName);
    var infos = await DeviceInformation.FindAllAsync(selector);
    if (infos.Count == 0)
    {
        Console.WriteLine("未匹配到经典蓝牙设备。");
        return 2;
    }

    var dev = await BluetoothDevice.FromIdAsync(infos[0].Id);
    if (dev is null)
    {
        Console.WriteLine("FromIdAsync 返回 null。");
        return 2;
    }

    var socket = await ConnectPrivateChannelAsync(dev, VivoPrivateUuid);
    if (socket is null)
    {
        return 3;
    }

    var writer = new DataWriter(socket.OutputStream);

    Console.WriteLine();
    Console.WriteLine("=== E5：不发送任何命令，交互监听 15 秒（遇 0x8509 自动应答） ===");
    ReportReceived("E5 被动监听", await CollectInteractiveAsync(socket, writer, 15000));

    var hs = BuildFrame(3, 0x000A, 0x0300, Array.Empty<byte>());
    Console.WriteLine();
    Console.WriteLine($"=== E1：V3 握手 {Convert.ToHexString(hs)} ===");
    await SendAsync(writer, hs);
    ReportReceived("E1 握手响应", await CollectInteractiveAsync(socket, writer, 6000));

    var q = BuildFrame(3, 0x001B, 0x0207, Array.Empty<byte>());
    Console.WriteLine();
    Console.WriteLine($"=== E3：V3 电量查询 {Convert.ToHexString(q)} ===");
    await SendAsync(writer, q);
    ReportReceived("E3 电量响应", await CollectInteractiveAsync(socket, writer, 10000));

    writer.DetachStream();
    socket.Dispose();
    Console.WriteLine();
    Console.WriteLine("=== E5/E1/E3 完成（只发送了握手、电量查询，以及被动的时间应答） ===");
    return 0;
}


// E2：注册通知链 → 长监听。
// 依据：上游 PROTOCOL.md §2「注册通知链 0x0202~0x0206 → 耳机回 0x82xx 后才会开始主动推送电量、佩戴等 report 帧」。
// 判定信号：注册 ACK（0x8202~0x8206）是否出现；ACK 出现即说明注册被接受，无需等 0x8207。
static async Task<int> ProbeRfcommRegisterAsync(string targetName, int listenMs)
{
    const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

    var selector = BluetoothDevice.GetDeviceSelectorFromDeviceName(targetName);
    var infos = await DeviceInformation.FindAllAsync(selector);
    if (infos.Count == 0)
    {
        Console.WriteLine("未匹配到经典蓝牙设备。");
        return 2;
    }

    var dev = await BluetoothDevice.FromIdAsync(infos[0].Id);
    if (dev is null)
    {
        Console.WriteLine("FromIdAsync 返回 null。");
        return 2;
    }

    var socket = await ConnectPrivateChannelAsync(dev, VivoPrivateUuid);
    if (socket is null)
    {
        return 3;
    }

    var writer = new DataWriter(socket.OutputStream);

    // v4 握手（实测有效，能收到 0x8300）
    var hs = BuildFrame(4, 0x000A, 0x0300, Array.Empty<byte>());
    Console.WriteLine();
    Console.WriteLine($"=== 握手 v4 {Convert.ToHexString(hs)} ===");
    await SendAsync(writer, hs);
    ReportReceived("握手响应", await CollectInteractiveAsync(socket, writer, 3000));

    // v4 注册链
    Console.WriteLine();
    Console.WriteLine("=== 注册通知链 v4：0x0202~0x0206（空载荷，逐条间隔 1.2s）===");
    for (var cmd = 0x0202; cmd <= 0x0206; cmd++)
    {
        var frame = BuildFrame(4, 0x001B, cmd, Array.Empty<byte>());
        Console.WriteLine($"--- 发送 {Convert.ToHexString(frame)} ---");
        await SendAsync(writer, frame);
        await Task.Delay(1200);
    }

    ReportReceived("v4 注册链后的响应", await CollectInteractiveAsync(socket, writer, 5000));

    // v3 注册链
    Console.WriteLine();
    Console.WriteLine("=== 注册通知链 v3：0x0202~0x0206 ===");
    for (var cmd = 0x0202; cmd <= 0x0206; cmd++)
    {
        var frame = BuildFrame(3, 0x001B, cmd, Array.Empty<byte>());
        Console.WriteLine($"--- 发送 {Convert.ToHexString(frame)} ---");
        await SendAsync(writer, frame);
        await Task.Delay(1200);
    }

    ReportReceived("v3 注册链后的响应", await CollectInteractiveAsync(socket, writer, 5000));

    // 注册被接受后再查询：验证「查询是否只在注册后才有效」
    foreach (var ver in new[] { 4, 3 })
    {
        var q = BuildFrame(ver, 0x001B, 0x0207, Array.Empty<byte>());
        Console.WriteLine();
        Console.WriteLine($"=== 注册后电量查询 v{ver} {Convert.ToHexString(q)} ===");
        await SendAsync(writer, q);
        ReportReceived($"注册后查询 v{ver} 的响应", await CollectInteractiveAsync(socket, writer, 5000));
    }

    // 长监听：看是否开始推送 0x8207
    Console.WriteLine();
    Console.WriteLine($"=== 注册后监听 {listenMs} ms：寻找 0x8207 电量帧 ===");
    ReportReceived("注册后监听", await CollectInteractiveAsync(socket, writer, listenMs));

    writer.DetachStream();
    socket.Dispose();
    Console.WriteLine();
    Console.WriteLine("=== E2 完成 ===");
    return 0;
}

// GAIA 帧的最小表示（类型声明必须放在所有顶级语句/本地函数之后）。
sealed record GFrame(int Version, int Flags, int Vendor, int Command, byte[] Payload);
