using System.Diagnostics;
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>
/// 常驻 RFCOMM（GAIA）可行性探针。
/// </summary>
/// <remarks>
/// <para><b>要回答的四个问题</b>（<c>docs/protocols/vivo-tws-3i.md</c> §21 之后尚未验证的部分）：</para>
/// <list type="number">
/// <item><description><b>T1 音频干扰</b>：持有 RFCOMM 连接期间，耳机听歌是否受影响。
/// 覆盖「端点消失/状态异常」这类粗粒度干扰（自动）+「卡顿/断音」这类细粒度干扰（人工听感）。</description></item>
/// <item><description><b>T2 断连行为</b>：耳机合盖断电后，socket 以什么方式死掉、耗时多久。
/// 这是断连检测逻辑的输入，<b>此前从未测过</b>。</description></item>
/// <item><description><b>T3 重连</b>：开盖后能否重新建立连接、耗时多少。</description></item>
/// <item><description><b>T4 保活</b>：持有连接期间耳机是否持续索要时间（<c>0x8509</c>）、
/// 链路是否会被静默掐断。</description></item>
/// </list>
/// <para>本工具是<b>实验探针，不是产品代码</b>：结果需人工判读，
/// 按 <c>AGENTS.md</c> §4 标记 <c>BLOCKED: requires physical device</c>。</para>
/// </remarks>
internal static class Program
{
    /// <summary>遥测 JSON 原文的落盘路径（仅第一帧）。</summary>
    private static string? TelemetryDumpPath;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var options = ParseOptions(args);
        Console.WriteLine("========================================================");
        Console.WriteLine(" 常驻 RFCOMM（GAIA）可行性探针");
        Console.WriteLine("========================================================");
        Console.WriteLine($"  目标设备   : {options.DeviceName}");
        Console.WriteLine($"  基线段     : {options.BaselineSec} 秒（未连接）");
        Console.WriteLine($"  保持段     : {options.HoldSec} 秒（常驻连接）");
        Console.WriteLine($"  合盖/开盖  : {(options.PowerCycle ? "启用" : "跳过")}");
        Console.WriteLine($"  采样间隔   : {options.SampleIntervalSec} 秒");
        Console.WriteLine($"  音频采样   : {(options.NoAudio ? "关闭" : "开启")}");
        Console.WriteLine();

        var report = new ProbeReport
        {
            RunAtLocal = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            DeviceName = options.DeviceName,
        };

        // ---------- 阶段 0：定位设备 ----------
        Console.WriteLine("=== 阶段 0：定位经典蓝牙设备 ===");
        BluetoothDevice? device;
        try
        {
            device = await FindDeviceAsync(options.DeviceName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"枚举设备失败：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        if (device is null)
        {
            Console.Error.WriteLine($"未找到经典蓝牙设备「{options.DeviceName}」，请检查设备名参数。");
            return 2;
        }

        report.InitialConnectionStatus = device.ConnectionStatus.ToString();
        report.Mac = FormatMac(device.BluetoothAddress);
        Console.WriteLine($"  找到设备，ConnectionStatus = {report.InitialConnectionStatus}");
        Console.WriteLine($"  经典蓝牙地址 = {report.Mac}");
        Console.WriteLine();

        // ---------- 阶段 1：基线段（未连接） ----------
        Console.WriteLine($"=== 阶段 1：基线段 {options.BaselineSec} 秒（尚未建立 RFCOMM 连接）===");
        Console.WriteLine("  这一段用于采集「没有连接干扰时」的系统状态，作为对照基线。");
        Console.WriteLine("  请现在让耳机连上电脑并放歌，保持 20~30 秒，注意听感是否平稳。");
        Console.WriteLine();

        var baselinePresence = new PresenceSampler(device, TimeSpan.FromSeconds(options.SampleIntervalSec));
        var baselineAudio = options.NoAudio
            ? null
            : new AudioMonitor(TimeSpan.FromSeconds(options.SampleIntervalSec * 5), options.DeviceName);

        baselinePresence.Start();
        baselineAudio?.Start();

        await Task.Delay(TimeSpan.FromSeconds(options.BaselineSec)).ConfigureAwait(false);

        report.Baseline = new SegmentInfo
        {
            DurationSec = options.BaselineSec,
            PresenceTransitions = baselinePresence.Transitions.ToList(),
            AudioChanges = baselineAudio?.Changes.ToList() ?? [],
            AudioMonitorAvailable = baselineAudio?.Available ?? false,
        };

        Console.WriteLine($"  基线段结束：蓝牙状态变化 {report.Baseline.PresenceTransitions.Count} 次，"
            + $"音频变化 {report.Baseline.AudioChanges.Count} 次");
        foreach (var change in report.Baseline.AudioChanges)
        {
            Console.WriteLine($"    - [{change.SecondsSinceStart:F1}s] {change.Kind} {change.Device} {change.Detail}");
        }

        await baselinePresence.DisposeAsync().ConfigureAwait(false);
        if (baselineAudio is not null)
        {
            await baselineAudio.DisposeAsync().ConfigureAwait(false);
        }

        Console.WriteLine();

        // ---------- 阶段 2：建立常驻连接 ----------
        Console.WriteLine("=== 阶段 2：建立常驻 RFCOMM 连接（连上后不关闭）===");
        var (session, timing, error) = await GaiaSession.ConnectAsync(
            options.DeviceName, report.Mac, Console.WriteLine).ConfigureAwait(false);

        if (session is null || timing is null)
        {
            Console.Error.WriteLine($"  建立连接失败：{error}");
            report.Notes.Add($"建立连接失败：{error}");
            report.WriteTo(options.OutputPath);
            Console.WriteLine($"  报告已写出：{options.OutputPath}");
            return 3;
        }

        report.Connect = timing;
        Console.WriteLine($"  建链总耗时 {timing.TotalMs} ms"
            + $"（SDP {timing.SdpMs} / 连接 {timing.ConnectMs} / 握手 {timing.HandshakeMs} / 注册 {timing.RegisterMs}）");
        Console.WriteLine();

        // ---------- 阶段 3：保持连接（常驻 + 干扰监测） ----------
        Console.WriteLine($"=== 阶段 3：保持连接 {options.HoldSec} 秒 ===");
        Console.WriteLine("  ★★ 现在请注意听感：耳机放歌是否出现 卡顿 / 断音 / 被踢下线？ ★★");
        Console.WriteLine("  程序会每 5 秒打印一次心跳，并报告任何系统状态变化。");
        Console.WriteLine("  期间可以取出/放回耳机来制造状态变化（观察 0x8207 实时到达）。");
        Console.WriteLine();

        var holdPresence = new PresenceSampler(device, TimeSpan.FromSeconds(options.SampleIntervalSec));
        var holdAudio = options.NoAudio
            ? null
            : new AudioMonitor(TimeSpan.FromSeconds(options.SampleIntervalSec * 5), options.DeviceName);

        holdPresence.Start();
        holdAudio?.Start();

        var holdSegment = new SegmentInfo { DurationSec = options.HoldSec };
        var holdStart = DateTimeOffset.UtcNow;

        using var holdCts = new CancellationTokenSource();
        var readTask = session.ReadLoopAsync(
            frame => OnFrame(frame, holdSegment, holdStart), holdCts.Token);

        var heartbeat = new Heartbeat(TimeSpan.FromSeconds(5), () => Console.WriteLine(
            $"  --- 心跳 t={(DateTimeOffset.UtcNow - holdStart).TotalSeconds:F0}s"
            + $" 帧计数={holdSegment.FramesByCommand.Count} 电池={holdSegment.BatteryFrames.Count}"
            + $" 索要时间={holdSegment.TimeRequests} 蓝牙={holdPresence.Current} ---"));

        DisconnectInfo? disconnect = null;
        var holdCompleted = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(options.HoldSec)))
            .ConfigureAwait(false);

        if (holdCompleted == readTask)
        {
            disconnect = await readTask.ConfigureAwait(false);
        }
        else
        {
            await holdCts.CancelAsync().ConfigureAwait(false);
            await readTask.ConfigureAwait(false);
        }

        heartbeat.Dispose();

        holdSegment.PresenceTransitions = holdPresence.Transitions.ToList();
        holdSegment.AudioChanges = holdAudio?.Changes.ToList() ?? [];
        holdSegment.AudioMonitorAvailable = holdAudio?.Available ?? false;
        report.Hold = holdSegment;

        Console.WriteLine($"  保持段结束：电量帧 {holdSegment.BatteryFrames.Count}，佩戴帧 {holdSegment.WearFrames}，"
            + $"索要时间 {holdSegment.TimeRequests} 次");
        Console.WriteLine($"  蓝牙状态变化 {holdSegment.PresenceTransitions.Count} 次，音频变化 {holdSegment.AudioChanges.Count} 次");
        foreach (var change in holdSegment.AudioChanges)
        {
            Console.WriteLine($"    - [{change.SecondsSinceStart:F1}s] {change.Kind} {change.Device} {change.Detail}");
        }

        foreach (var transition in holdSegment.PresenceTransitions)
        {
            Console.WriteLine($"    - [{transition.SecondsSinceStart:F1}s] 蓝牙状态 {transition.From} → {transition.To}");
        }

        await holdPresence.DisposeAsync().ConfigureAwait(false);
        if (holdAudio is not null)
        {
            await holdAudio.DisposeAsync().ConfigureAwait(false);
        }

        Console.WriteLine();

        // ---------- 阶段 4/5：合盖断电与重连 ----------
        if (options.PowerCycle)
        {
            var cycle = new PowerCycleInfo
            {
                PresenceTransitions = holdSegment.PresenceTransitions,
            };

            if (disconnect is not null)
            {
                Console.WriteLine("=== 阶段 4：断连已发生（连接在保持段内就断了）===");
                FillDisconnect(cycle, disconnect);
            }
            else
            {
                Console.WriteLine("=== 阶段 4：请现在合上充电盒盖（耳机断电），观察 socket 何时断 ===");
                Console.WriteLine($"    最多等待 {options.PowerCycleWaitSec} 秒");
                Console.WriteLine();

                var waitWatch = Stopwatch.StartNew();
                var deadline = DateTimeOffset.Now.AddSeconds(options.PowerCycleWaitSec);

                while (disconnect is null && DateTimeOffset.Now < deadline)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }

                if (disconnect is not null)
                {
                    FillDisconnect(cycle, disconnect);
                }
                else
                {
                    cycle.DisconnectDetected = false;
                    cycle.DisconnectReason = "timeout";
                    Console.WriteLine($"  ✗ {options.PowerCycleWaitSec} 秒内没有检测到断连"
                        + "（socket 静默挂死，需要靠上层超时兜底）");
                }
            }

            Console.WriteLine();

            // ---------- 阶段 5：开盖重连 ----------
            Console.WriteLine("=== 阶段 5：请现在打开充电盒盖（耳机通电），观察能否自动重连 ===");
            Console.WriteLine();

            var reconnectWatch = Stopwatch.StartNew();
            var attempt = 0;

            while (reconnectWatch.Elapsed.TotalSeconds < options.ReconnectWaitSec)
            {
                attempt++;
                var attemptResult = new List<string>();
                var (newSession, newTiming, newError) = await GaiaSession.ConnectAsync(
                    options.DeviceName, report.Mac, message => attemptResult.Add(message)).ConfigureAwait(false);

                cycle.ReconnectAttempts = attempt;
                cycle.ReconnectAttemptsLog.Add(
                    $"尝试 #{attempt}（t={reconnectWatch.Elapsed.TotalSeconds:F1}s）：{newError ?? "连接成功"}");

                if (newSession is not null && newTiming is not null)
                {
                    cycle.ReconnectSucceeded = true;
                    cycle.ReconnectAfterSec = Math.Round(reconnectWatch.Elapsed.TotalSeconds, 2);
                    cycle.ReconnectTiming = newTiming;
                    Console.WriteLine($"  ✓ 第 {attempt} 次尝试成功，耗时 {cycle.ReconnectAfterSec:F1} 秒"
                        + $"（建链 {newTiming.TotalMs} ms）");

                    // 重连后短暂监听，确认新会话真的能收帧。
                    using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var reconnected = newSession.ReadLoopAsync(
                        frame => Console.WriteLine($"    [重连后] {frame.Summary}"), reconnectCts.Token);
                    await Task.WhenAny(reconnected, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                    await reconnectCts.CancelAsync().ConfigureAwait(false);
                    newSession.Dispose();
                    break;
                }

                Console.WriteLine($"  ✗ 第 {attempt} 次尝试失败：{newError}");

                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            if (!cycle.ReconnectSucceeded)
            {
                Console.WriteLine($"  ✗ {options.ReconnectWaitSec} 秒内未能重连");
            }

            report.PowerCycle = cycle;
            Console.WriteLine();
        }

        session.Dispose();

        // ---------- 阶段 6：汇总 ----------
        Console.WriteLine("=== 阶段 6：汇总 ===");
        Console.WriteLine($"  帧总数分布：{string.Join(", ", holdSegment.FramesByCommand.OrderBy(k => k.Key).Select(k => $"0x{k.Key:X4}×{k.Value}"))}");
        Console.WriteLine($"  遥测帧 {holdSegment.TelemetryFrames}，固件版本 {holdSegment.TelemetryFirmware ?? "?"}");

        if (holdSegment.TelemetryAudioStates.Count > 0)
        {
            Console.WriteLine("  耳机自报音频链路状态（A2dp/Sco/Mute）的变化：");
            foreach (var change in holdSegment.TelemetryAudioStates)
            {
                Console.WriteLine($"    - [{change.SecondsSinceStart:F1}s] {change.Before} → {change.After}");
            }
        }
        else
        {
            Console.WriteLine("  耳机自报音频链路状态：无变化记录（无遥测帧或状态恒定）");
        }

        // 0x8207 与遥测 /C 的交叉验证：两个独立来源的电量应逐值一致。
        var lastGaiaBattery = holdSegment.BatteryFrames.LastOrDefault();
        var lastTelemetryBattery = holdSegment.TelemetryBatterySamples.LastOrDefault();
        if (lastGaiaBattery is not null && lastTelemetryBattery is not null)
        {
            var expected = $"{lastGaiaBattery.Left}_{lastGaiaBattery.Right}";
            var consistent = string.Equals(expected, lastTelemetryBattery, StringComparison.Ordinal);
            Console.WriteLine($"  电量交叉验证：0x8207 = {expected}，遥测 /C = {lastTelemetryBattery}"
                + $" → {(consistent ? "一致 ✓" : "不一致 ✗")}");
            if (!consistent)
            {
                report.Notes.Add($"电量交叉验证不一致：0x8207={expected}，遥测/C={lastTelemetryBattery}");
            }
        }

        report.Notes.Add("自动检测覆盖：经典蓝牙链路状态、音频端点/声卡状态变化、耳机自报音频链路状态（A2dp/Sco/Mute）。");
        report.Notes.Add("音频卡顿/断音属亚秒级问题，本工具不自动判定，需人工听感结论（见控制台时间线）。");
        report.Notes.Add("0x8509 时间请求已自动应答——常驻连接必须实现，否则耳机可能停止继续交互。");
        if (holdSegment.TelemetryFirmware is not null)
        {
            report.Notes.Add($"遥测帧报出固件版本：{holdSegment.TelemetryFirmware}（此前一直标记为待核实）");
        }

        report.WriteTo(options.OutputPath);
        Console.WriteLine($"  报告已写出：{options.OutputPath}");
        Console.WriteLine();
        Console.WriteLine("  请把本次的听感结论告诉 AI（或手动编辑报告的 notes 字段），用于决定是否推进常驻连接。");

        return 0;
    }

    private static void OnFrame(GaiaFrameEvent frame, SegmentInfo segment, DateTimeOffset segmentStart)
    {
        var key = $"0x{frame.Command:X4}";
        segment.FramesByCommand[key] = segment.FramesByCommand.GetValueOrDefault(key) + 1;

        switch (frame.Kind)
        {
            case GaiaFrameKind.Battery:
                segment.BatteryFrames.Add(new BatterySample(
                    frame.At,
                    Math.Round((frame.At - segmentStart).TotalSeconds, 2),
                    frame.Payload.Length > 1 ? frame.Payload[1] : -1,
                    frame.Payload.Length > 2 ? frame.Payload[2] : -1,
                    frame.Payload.Length > 3 ? frame.Payload[3] : -1,
                    frame.Payload.Length > 4 ? frame.Payload[4] : -1));
                Console.WriteLine($"  [{frame.SecondsSinceConnect:F1}s] ★ 电量帧 {frame.Summary}");
                break;

            case GaiaFrameKind.Wear:
                segment.WearFrames++;
                Console.WriteLine($"  [{frame.SecondsSinceConnect:F1}s] 佩戴帧 {frame.Summary}");
                break;

            case GaiaFrameKind.Telemetry:
                segment.TelemetryFrames++;
                RecordTelemetry(frame, segment, segmentStart);
                break;

            case GaiaFrameKind.TimeRequest:
                segment.TimeRequests++;
                break;

            default:
                Console.WriteLine($"  [{frame.SecondsSinceConnect:F1}s] 帧 {key} {frame.Summary}");
                break;
        }
    }

    /// <summary>
    /// 记录遥测帧里的音频链路状态，并在变化时留下记录。
    /// </summary>
    /// <remarks>
    /// 这是 T1（音频干扰）的第一手证据：状态来自<b>耳机自己</b>，
    /// 比 Windows 侧的 PnP 端点快照更贴近「这段经典蓝牙链路有没有被影响」。
    /// </remarks>
    private static void RecordTelemetry(GaiaFrameEvent frame, SegmentInfo segment, DateTimeOffset segmentStart)
    {
        if (!TelemetryParser.TryParse(frame.Payload, out var sample, out var json) || sample is null)
        {
            return;
        }

        if (json is not null && TelemetryDumpPath is not null)
        {
            var dumpPath = TelemetryDumpPath;
            TelemetryDumpPath = null;
            try
            {
                File.WriteAllText(dumpPath, json);
                Console.WriteLine($"  遥测 JSON 原文已写出：{dumpPath}");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"  遥测 JSON 写出失败：{ex.Message}");
            }
        }

        var state = $"{sample.AudioLink.A2dp}/{sample.AudioLink.Sco}/{sample.AudioLink.Mute}";

        if (sample.Firmware is not null)
        {
            segment.TelemetryFirmware = sample.Firmware;
        }

        if (sample.BatteryCsv is not null)
        {
            segment.TelemetryBatterySamples.Add(sample.BatteryCsv);
        }

        var last = segment.TelemetryAudioStates.LastOrDefault();
        if (last is not null && last.After == state)
        {
            return;
        }

        var before = last?.After ?? "(无)";
        segment.TelemetryAudioStates.Add(new TelemetryAudioStateChange(
            frame.At,
            Math.Round((frame.At - segmentStart).TotalSeconds, 2),
            before,
            state));

        Console.WriteLine($"  [{frame.SecondsSinceConnect:F1}s] 遥测帧：音频链路 A2dp/Sco/Mute {before} → {state}"
            + $"（电量={sample.BatteryCsv ?? "?"} 固件={sample.Firmware ?? "?"}）");
    }

    private static void FillDisconnect(PowerCycleInfo cycle, DisconnectInfo disconnect)
    {
        cycle.DisconnectDetected = true;
        cycle.DisconnectAfterSec = disconnect.SecondsSinceConnect;
        cycle.DisconnectReason = disconnect.Reason;
        cycle.DisconnectExceptionType = disconnect.ExceptionType;
        cycle.DisconnectHResult = disconnect.HResult;
        cycle.DisconnectMessage = disconnect.Message;

        Console.WriteLine($"  ✓ 检测到断连：建立连接后 {disconnect.SecondsSinceConnect:F1} 秒");
        Console.WriteLine($"    原因={disconnect.Reason} 异常类型={disconnect.ExceptionType} HResult={disconnect.HResult}");
        Console.WriteLine($"    信息={disconnect.Message}");
    }

    private static async Task<BluetoothDevice?> FindDeviceAsync(string deviceName)
    {
        var all = await DeviceInformation
            .FindAllAsync(BluetoothDevice.GetDeviceSelector())
            .AsTask().ConfigureAwait(false);

        foreach (var info in all)
        {
            if (info.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return await BluetoothDevice.FromIdAsync(info.Id).AsTask().ConfigureAwait(false);
            }
        }

        return null;
    }

    private static string FormatMac(ulong address)
    {
        var parts = new List<string>();
        for (var shift = 40; shift >= 0; shift -= 8)
        {
            parts.Add(((address >> shift) & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
        }

        return string.Join(':', parts);
    }

    private static ProbeOptions ParseOptions(string[] args)
    {
        var options = new ProbeOptions();
        var index = 0;

        while (index < args.Length)
        {
            var arg = args[index];
            var next = index + 1 < args.Length ? args[index + 1] : null;

            switch (arg)
            {
                case "--name" when next is not null:
                    options.DeviceName = next;
                    index += 2;
                    continue;
                case "--baseline" when next is not null && int.TryParse(next, out var baseline):
                    options.BaselineSec = baseline;
                    index += 2;
                    continue;
                case "--hold" when next is not null && int.TryParse(next, out var hold):
                    options.HoldSec = hold;
                    index += 2;
                    continue;
                case "--power-cycle-wait" when next is not null && int.TryParse(next, out var wait):
                    options.PowerCycleWaitSec = wait;
                    index += 2;
                    continue;
                case "--reconnect-wait" when next is not null && int.TryParse(next, out var reconnect):
                    options.ReconnectWaitSec = reconnect;
                    index += 2;
                    continue;
                case "--interval" when next is not null && int.TryParse(next, out var interval):
                    options.SampleIntervalSec = Math.Max(1, interval);
                    index += 2;
                    continue;
                case "--out" when next is not null:
                    options.OutputPath = next;
                    index += 2;
                    continue;
                case "--dump-telemetry" when next is not null:
                    TelemetryDumpPath = next;
                    index += 2;
                    continue;
                case "--no-power-cycle":
                    options.PowerCycle = false;
                    index++;
                    continue;
                case "--no-audio":
                    options.NoAudio = true;
                    index++;
                    continue;
                default:
                    index++;
                    continue;
            }
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            常驻 RFCOMM（GAIA）可行性探针 —— vivo TWS 3i

            用法：
              RfcommResidentProbe [选项]

            选项：
              --name <名称>            经典蓝牙设备名（默认 "vivo TWS 3i"）
              --baseline <秒>          基线段时长，默认 20
              --hold <秒>              常驻保持时长，默认 180
              --power-cycle-wait <秒>  合盖后等待断连的窗口，默认 90
              --reconnect-wait <秒>    开盖后等待重连的窗口，默认 120
              --interval <秒>          状态采样间隔，默认 1
              --out <路径>             报告 JSON 输出路径
              --dump-telemetry <路径>  把第一帧遥测 JSON 原文落盘（用于核对字段名）
              --no-power-cycle         跳过合盖/开盖环节
              --no-audio               关闭音频端点采样
            """);
    }

    private sealed class ProbeOptions
    {
        public string DeviceName { get; set; } = "vivo TWS 3i";
        public int BaselineSec { get; set; } = 20;
        public int HoldSec { get; set; } = 180;
        public int PowerCycleWaitSec { get; set; } = 90;
        public int ReconnectWaitSec { get; set; } = 120;
        public int SampleIntervalSec { get; set; } = 1;
        public bool PowerCycle { get; set; } = true;
        public bool NoAudio { get; set; }
        public string OutputPath { get; set; } = Path.Combine(
            Environment.CurrentDirectory,
            $"rfcomm-resident-probe-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    /// <summary>定时打印心跳的辅助器。</summary>
    private sealed class Heartbeat : IDisposable
    {
        private readonly Timer _timer;

        public Heartbeat(TimeSpan period, Action onTick)
        {
            _timer = new Timer(_ => onTick(), null, period, period);
        }

        public void Dispose() => _timer.Dispose();
    }
}
