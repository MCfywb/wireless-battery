// DualConnCapture：被动 BLE 广播采集（只读），用于验证「双连接」对电量可读性与广播频率的影响。
// 设计要点（隔离变量）：
//   - 全程只做被动扫描，不连接设备、不写任何数据、不做任何 GAIA/RFCOMM 交互。
//   - 只采集目标型号（modelIds 132/133）的 TwsParserV1 广播，复用 VivoTws3i.Protocol 的已验证偏移。
//   - 每一条广播都写入 CSV（含高精度相对时间戳 + 本地墙钟时间 + RSSI + 原始字节 + 解析字段），
//     以便事后按「手机蓝牙开/关」的切换时刻切分时间段，分别统计频率与电量可读性。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation;
using VivoTws3i.Protocol;

var listenMs = 600000; // 默认 10 分钟，足够覆盖「手机蓝牙关→开」的完整过程
var outIdx = Array.IndexOf(args, "--listen-ms");
if (outIdx >= 0 && outIdx + 1 < args.Length && int.TryParse(args[outIdx + 1], out var parsed))
{
    listenMs = parsed;
}

string? outArg = null;
var outEqIdx = Array.IndexOf(args, "--out");
if (outEqIdx >= 0 && outEqIdx + 1 < args.Length && !args[outEqIdx + 1].StartsWith("--", StringComparison.Ordinal))
{
    outArg = args[outEqIdx + 1];
}
else
{
    outArg = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?.Substring("--out=".Length);
}
var outPath = outArg ?? Path.Combine("testdata", $"dualconn-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

var dir = Path.GetDirectoryName(outPath);
if (!string.IsNullOrEmpty(dir))
{
    Directory.CreateDirectory(dir);
}

const ushort CompanyId = 0x0837;
var supportedModels = new HashSet<int> { 132, 133 };

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== DualConnCapture（只读 BLE 广播，验证双连接对电量/频率的影响）===");
Console.WriteLine($"监听时长: {listenMs / 1000.0:F0}s   输出: {outPath}");
Console.WriteLine($"companyId=0x{CompanyId:X4}   目标型号 modelIds={{132,133}}");
Console.WriteLine("控制规则：耳机离开充电盒且保持静止；全程只由你切换手机蓝牙开/关。");
Console.WriteLine($"开始墙钟时间: {DateTime.Now:HH:mm:ss.fff}");
Console.WriteLine();

var stopwatch = Stopwatch.StartNew();
var gate = new object();

var totalCompany = 0;
var parsedCount = 0;
var ignoredCompany = 0;

// 最新读数（用于实时状态行）
int? lastLeft = null, lastRight = null, lastCase = null;
int? lastRssi = null;
byte lastState = 0;
int lastPairState = -1;
bool? lastConnected = null;
long lastRowElapsed = -1;

using var writer = new StreamWriter(outPath, false, Encoding.UTF8) { AutoFlush = true };
writer.WriteLine("ElapsedMs,LocalTime,Rssi,RawHex,Format,ModelId,Left,Right,Case,LeftChg,RightChg,CaseChg,StateByte,PairState,Connected,LeftInCase,RightInCase,BaseClosed");

var watcher = new BluetoothLEAdvertisementWatcher();
// 关闭采样间隔过滤，确保每一条广播事件都被记录（频率统计需要原始接收计数）
watcher.SignalStrengthFilter.SamplingInterval = TimeSpan.Zero;

TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> onReceived =
    (_, e) =>
    {
        foreach (var md in e.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != CompanyId)
            {
                continue;
            }

            var raw = new byte[md.Data.Length];
            using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(md.Data))
            {
                dr.ReadBytes(raw);
            }

            lock (gate)
            {
                totalCompany++;
                VivoTwsParseStatus status;
                VivoTwsAdvertisement? adv;
                if (!VivoTwsAdvertisementParser.TryParse(raw.AsSpan(), out adv, out status)
                    || adv is null
                    || !supportedModels.Contains(adv.ModelId))
                {
                    ignoredCompany++;
                    return;
                }

                parsedCount++;
                var elapsed = stopwatch.ElapsedMilliseconds;
                var local = DateTime.Now;
                var rssi = e.RawSignalStrengthInDBm;
                var state = raw.Length > 8 ? raw[8] : (byte)0;

                lastLeft = adv.LeftEarbudBattery;
                lastRight = adv.RightEarbudBattery;
                lastCase = adv.ChargingCaseBattery;
                lastRssi = rssi;
                lastState = state;
                lastPairState = adv.PairState;
                lastConnected = adv.ConnectedState;
                lastRowElapsed = elapsed;

                var b = (bool? v) => v is null ? "" : (v.Value ? "1" : "0");
                writer.WriteLine(
                    $"{elapsed},{local:HH:mm:ss.fff},{rssi},{Convert.ToHexString(raw)},{adv.Format},{adv.ModelId}," +
                    $"{adv.LeftEarbudBattery ?? -1},{adv.RightEarbudBattery ?? -1},{adv.ChargingCaseBattery ?? -1}," +
                    $"{b(adv.LeftEarbudCharging)},{b(adv.RightEarbudCharging)},{b(adv.ChargingCaseCharging)}," +
                    $"0x{state:X2},{adv.PairState},{b(adv.ConnectedState)},{b(adv.LeftEarbudInCase)},{b(adv.RightEarbudInCase)},{b(adv.BaseClosed)}");
            }
        }
    };

var cancelled = false;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancelled = true;
};

watcher.Received += onReceived;
watcher.Start();

var lastTicker = -10L;
while (stopwatch.ElapsedMilliseconds < listenMs && !cancelled)
{
    var sec = (long)(stopwatch.ElapsedMilliseconds / 1000);
    if (sec - lastTicker >= 5 || sec < 6)
    {
        lastTicker = sec;
        lock (gate)
        {
            var bt = lastLeft is null && lastRight is null && lastCase is null
                ? "(暂无)"
                : $"{lastLeft ?? -1}/{lastRight ?? -1}/{lastCase ?? -1}";
            Console.WriteLine(
                $"t={sec,4}s | 0x0837 累计 {totalCompany,5}  目标解析 {parsedCount,5}  忽略 {ignoredCompany,4} | " +
                $"电量 {bt} | 状态字节 0x{lastState:X2} PairState={lastPairState} 连接={lastConnected} | RSSI {lastRssi} | 末行@{lastRowElapsed}ms");
        }
    }

    await Task.Delay(1000);
}

watcher.Stop();
lock (gate)
{
    Console.WriteLine();
    Console.WriteLine("=== 采集结束 ===");
    Console.WriteLine($"总时长: {stopwatch.ElapsedMilliseconds / 1000.0:F0}s");
    Console.WriteLine($"0x0837 帧累计: {totalCompany}   目标型号解析: {parsedCount}   忽略(非目标): {ignoredCompany}");
    Console.WriteLine($"CSV: {Path.GetFullPath(outPath)}");
}
