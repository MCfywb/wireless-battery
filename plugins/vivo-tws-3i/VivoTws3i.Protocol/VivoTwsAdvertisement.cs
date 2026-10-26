namespace VivoTws3i.Protocol;

/// <summary>
/// vivo Fast Pair 广播格式。格式与偏移由官方 App 源码确认：
/// <c>com.android.vivo.tws.fastpair.parse.parser.TwsParserV1</c> 与
/// <c>n2/e.java</c>（见 docs/protocols/vivo-tws-3i.md §16、§17）。
/// </summary>
public enum VivoTwsAdvertisementFormat
{
    Unknown = 0,

    /// <summary>TWS 对，格式 V1。本插件目标型号使用此格式。</summary>
    TwsV1,

    /// <summary>TWS 对，格式 V2（model id 支持 16 位扩展）。</summary>
    TwsV2,

    /// <summary>TWS 对，V2 的扩展广播（扫描响应带长设备名）。</summary>
    TwsAdvertise,

    /// <summary>单耳机，格式 V2（无右耳、无充电位）。</summary>
    SingleV2,

    /// <summary>运动耳机（单耳）V1。</summary>
    WirelessSportV1,
}

/// <summary>解析结果状态。</summary>
public enum VivoTwsParseStatus
{
    Ok = 0,

    /// <summary>载荷为空。</summary>
    EmptyPayload,

    /// <summary>不是 vivo TWS 广播（payload[0] 既不是 0x08 也不是 0x09）。</summary>
    NotTwsAdvertisement,

    /// <summary>格式版本不支持。</summary>
    UnsupportedVersion,

    /// <summary>长度不足。</summary>
    TooShort,
}

/// <summary>
/// 一条已解析的 vivo TWS 广播。
/// </summary>
/// <remarks>
/// 电量字段为 <c>null</c> 表示设备未提供或数值无效，<b>绝不</b>用 0 代替。
/// </remarks>
public sealed record VivoTwsAdvertisement
{
    /// <summary>设备 MAC（来自 payload[2..7]）。仅本地使用，不得写入导出诊断包。</summary>
    public required string MacAddress { get; init; }

    /// <summary>型号 id（payload[13]，V1 格式为单字节）。</summary>
    public required int ModelId { get; init; }

    /// <summary>左耳电量 %，未知为 null。</summary>
    public int? LeftEarbudBattery { get; init; }

    /// <summary>右耳电量 %，未知为 null。</summary>
    public int? RightEarbudBattery { get; init; }

    /// <summary>充电盒电量 %，未知为 null。</summary>
    public int? ChargingCaseBattery { get; init; }

    /// <summary>左耳是否充电中；协议未报告时为 null。</summary>
    public bool? LeftEarbudCharging { get; init; }

    /// <summary>右耳是否充电中；协议未报告时为 null。</summary>
    public bool? RightEarbudCharging { get; init; }

    /// <summary>充电盒是否充电中；协议未报告时为 null。</summary>
    public bool? ChargingCaseCharging { get; init; }

    /// <summary>左耳是否在盒内；协议未报告时为 null。</summary>
    public bool? LeftEarbudInCase { get; init; }

    /// <summary>右耳是否在盒内；协议未报告时为 null。</summary>
    public bool? RightEarbudInCase { get; init; }

    /// <summary>充电盒基座是否闭合；协议未报告时为 null。</summary>
    public bool? BaseClosed { get; init; }

    /// <summary>
    /// 耳机是否处于已连接态（来自广播的 mPairState 位域）。
    /// 注意该位只表示「连着某个客户端」，不区分连接对象，也不影响电量可读性。
    /// </summary>
    public bool? ConnectedState { get; init; }

    /// <summary>原始配对/连接状态位域值（2 字节中的高 2 位）。</summary>
    public int PairState { get; init; }

    /// <summary>该广播使用的格式。</summary>
    public VivoTwsAdvertisementFormat Format { get; init; } = VivoTwsAdvertisementFormat.Unknown;

    /// <summary>解析过程中的诊断信息（对应 §4.2「记录诊断而非钳制」）。</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
