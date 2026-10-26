namespace VivoTws3i.Protocol;

/// <summary>解析诊断码。用于把「读不到」与「读到但无效」区分开（§4.2）。</summary>
public static class VivoTwsDiagnostics
{
    /// <summary>某部件电量字节为 0xFF：设备未提供该部件读数。</summary>
    public const string ComponentNotReported = "COMPONENT_NOT_REPORTED";

    /// <summary>电量字节超出 0..100：记录诊断，不钳制（§4.2）。</summary>
    public const string BatteryOutOfRange = "BATTERY_OUT_OF_RANGE";

    /// <summary>该广播不是 vivo TWS 格式（payload[0] 不是 0x08/0x09）。</summary>
    public const string NotAVivoTwsAdvertisement = "NOT_A_VIVO_TWS_ADVERTISEMENT";

    /// <summary>广播格式版本不受支持。</summary>
    public const string UnsupportedFormatVersion = "UNSUPPORTED_FORMAT_VERSION";

    /// <summary>广播长度不足，无法解析。</summary>
    public const string PayloadTooShort = "PAYLOAD_TOO_SHORT";

    /// <summary>型号 id 不在插件支持列表内。</summary>
    public const string ModelNotSupported = "MODEL_NOT_SUPPORTED";
}
