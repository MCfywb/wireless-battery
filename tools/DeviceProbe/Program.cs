// Phase 0 只读探针：枚举目标 BLE 设备的服务 / 特征 / 描述符，并读取所有可读特征。
// 严格遵守「只读」约束：本文件不含任何 GATT 写操作，不发送配对 / 设置 / 固件类命令。
// 输出可能包含设备地址，仅打印到控制台，不落盘；写入仓库文档前必须脱敏。
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
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
