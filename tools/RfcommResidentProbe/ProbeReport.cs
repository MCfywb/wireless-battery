using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>探针报告。</summary>
internal sealed class ProbeReport
{
    /// <summary>工具名与版本标记。</summary>
    public string Tool { get; set; } = "RfcommResidentProbe";

    /// <summary>运行时刻（本机时间）。</summary>
    public string RunAtLocal { get; set; } = string.Empty;

    /// <summary>目标设备名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>目标设备经典蓝牙地址。</summary>
    public string? Mac { get; set; }

    /// <summary>连接建立前的系统蓝牙状态。</summary>
    public string InitialConnectionStatus { get; set; } = "?";

    /// <summary>建链各阶段耗时。</summary>
    public ConnectTiming? Connect { get; set; }

    /// <summary>基线段（未建立连接）。</summary>
    public SegmentInfo Baseline { get; set; } = new();

    /// <summary>保持段（常驻连接中）。</summary>
    public SegmentInfo Hold { get; set; } = new();

    /// <summary>合盖/开盖环节。</summary>
    public PowerCycleInfo? PowerCycle { get; set; }

    /// <summary>结论与遗留问题。</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>写出 JSON 报告。</summary>
    public void WriteTo(string path)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }
}

/// <summary>一个采样段的统计。</summary>
internal sealed class SegmentInfo
{
    /// <summary>段时长（秒）。</summary>
    public double DurationSec { get; set; }

    /// <summary>收到的各类帧计数。</summary>
    public Dictionary<string, int> FramesByCommand { get; set; } = new(StringComparer.Ordinal);

    /// <summary>电量帧明细（含原始载荷）。</summary>
    public List<BatterySample> BatteryFrames { get; set; } = [];

    /// <summary>佩戴帧明细（含原始载荷）。</summary>
    public List<WearSample> WearSamples { get; set; } = [];

    /// <summary>佩戴帧数量。</summary>
    public int WearFrames { get; set; }

    /// <summary>耳机索要时间的次数（已自动应答）。</summary>
    public int TimeRequests { get; set; }

    /// <summary>系统蓝牙状态变化。</summary>
    public List<PresenceTransition> PresenceTransitions { get; set; } = [];

    /// <summary>音频端点/声卡变化。</summary>
    public List<AudioChange> AudioChanges { get; set; } = [];

    /// <summary>音频监视是否可用。</summary>
    public bool AudioMonitorAvailable { get; set; }

    /// <summary>遥测帧（0x8224）中耳机自报的音频链路状态变化。</summary>
    public List<TelemetryAudioStateChange> TelemetryAudioStates { get; set; } = [];

    /// <summary>遥测帧数量。</summary>
    public int TelemetryFrames { get; set; }

    /// <summary>遥测帧报出的固件版本（根节点 /V）。</summary>
    public string? TelemetryFirmware { get; set; }

    /// <summary>遥测帧报出的电量（根节点 /C，形如 <c>93_90</c>），用于与 0x8207 交叉验证。</summary>
    public List<string> TelemetryBatterySamples { get; set; } = [];
}

/// <summary>耳机自报的音频链路状态的一次变化。</summary>
/// <param name="At">时刻。</param>
/// <param name="SecondsSinceStart">相对本段开始的秒数。</param>
/// <param name="Before">变化前状态。</param>
/// <param name="After">变化后状态。</param>
internal sealed record TelemetryAudioStateChange(
    DateTimeOffset At,
    double SecondsSinceStart,
    string Before,
    string After);

/// <summary>一帧电量读数。</summary>
/// <param name="At">时刻。</param>
/// <param name="SecondsSinceStart">相对本段开始的秒数。</param>
/// <param name="Left">左耳电量（0xFF 表示未知）。</param>
/// <param name="Right">右耳电量（0xFF 表示未知）。</param>
/// <param name="Case">充电盒电量（0xFF 表示未知）。</param>
/// <param name="ChargeBits">充电位字节。</param>
/// <param name="RawPayload">
/// 原始载荷十六进制。
/// <b>为什么必须存原值</b>：2026-10-27 首次测试发现盒电量会在 <c>94</c> 与 <c>0xFF</c> 之间
/// 成段跳变，只存解码后的值无法判断成因（是载荷结构不同，还是设备真在报未知），
/// 必须保留原始字节供离线比对。
/// </param>
internal sealed record BatterySample(
    DateTimeOffset At,
    double SecondsSinceStart,
    int Left,
    int Right,
    int Case,
    int ChargeBits,
    string RawPayload);

/// <summary>
/// 一帧佩戴状态（<c>0x820D</c>）读数。
/// </summary>
/// <remarks>
/// 记录它同样是为了解释盒电量的跳变：佩戴/开合状态与 <c>0x8207</c> 的盒值几乎一一配对，
/// 把两路原始载荷按时间对齐比对，才能判断跳变是随状态变化还是随机。
/// </remarks>
internal sealed record WearSample(
    DateTimeOffset At,
    double SecondsSinceStart,
    string StateHex,
    string RawPayload);

/// <summary>合盖/开盖环节的结果。</summary>
internal sealed class PowerCycleInfo
{
    /// <summary>断连是否被检测到。</summary>
    public bool DisconnectDetected { get; set; }

    /// <summary>断连发生在建立连接后多少秒。</summary>
    public double DisconnectAfterSec { get; set; }

    /// <summary>断连原因分类：eof（读到 0 字节）/ error（底层异常）/ timeout（超时未断）。</summary>
    public string DisconnectReason { get; set; } = string.Empty;

    /// <summary>断连时的异常类型全名。</summary>
    public string? DisconnectExceptionType { get; set; }

    /// <summary>断连时的异常 HResult。</summary>
    public string? DisconnectHResult { get; set; }

    /// <summary>断连时的异常信息。</summary>
    public string? DisconnectMessage { get; set; }

    /// <summary>断连前后系统蓝牙状态的变化。</summary>
    public List<PresenceTransition> PresenceTransitions { get; set; } = [];

    /// <summary>重连是否成功。</summary>
    public bool ReconnectSucceeded { get; set; }

    /// <summary>重连尝试次数。</summary>
    public int ReconnectAttempts { get; set; }

    /// <summary>首次重连成功耗时（秒）；失败时为 -1。</summary>
    public double ReconnectAfterSec { get; set; } = -1;

    /// <summary>重连后的建链耗时明细。</summary>
    public ConnectTiming? ReconnectTiming { get; set; }

    /// <summary>每次重连尝试的原始结果。</summary>
    public List<string> ReconnectAttemptsLog { get; set; } = [];
}
