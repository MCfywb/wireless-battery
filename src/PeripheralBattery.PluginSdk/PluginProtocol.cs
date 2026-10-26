using System.Text.Json.Serialization;

namespace PeripheralBattery.PluginSdk;

/// <summary>
/// Worker 与宿主之间的 JSON Lines 契约。取自 docs/development-plan.md §5.3。
/// </summary>
/// <remarks>
/// 宿主向 Worker 的 stdin 写一行请求，从 stdout 读一行响应；
/// 每行一个独立消息，首版最大 256 KiB。stdout 不得夹杂非 JSON 文本。
/// </remarks>
public static class PluginProtocol
{
    /// <summary>协议版本；不兼容变更时递增。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>单条消息的最大字节数（256 KiB）。</summary>
    public const int MaxMessageBytes = 256 * 1024;
}

/// <summary>宿主 → Worker 的方法名。</summary>
public static class PluginMethods
{
    public const string Discover = "discover";
    public const string Poll = "poll";
    public const string Diagnostics = "diagnostics";
    public const string Shutdown = "shutdown";
}

/// <summary>请求体。</summary>
public sealed class PluginRequest
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = PluginProtocol.ProtocolVersion;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; set; }
}

/// <summary>响应中的单个部件读数。</summary>
public sealed class PluginReading
{
    /// <summary>
    /// 设备标识。宿主据此把读数归并到具体设备。
    /// </summary>
    /// <remarks>
    /// 兼容新增字段：早期 Worker 不返回该字段时，宿主回退为「插件内唯一设备」或「插件 id + default」，
    /// 不得把不同设备的读数错误归并（§4.2 按稳定设备 ID 去重）。
    /// </remarks>
    [JsonPropertyName("deviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; set; }

    /// <summary>设备显示名；缺失时宿主回退为设备标识。</summary>
    [JsonPropertyName("deviceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }

    /// <summary>部件名，取 <see cref="BatteryComponent"/> 的 camelCase 形式。</summary>
    [JsonPropertyName("component")]
    public string Component { get; set; } = string.Empty;

    /// <summary>电量百分比；未知时为 <c>null</c>，绝不写 0 冒充。</summary>
    [JsonPropertyName("percentage")]
    public int? Percentage { get; set; }

    [JsonPropertyName("charging")]
    public bool? Charging { get; set; }

    [JsonPropertyName("connected")]
    public bool? Connected { get; set; }

    [JsonPropertyName("quality")]
    public string Quality { get; set; } = nameof(ReadingQuality.Unavailable);

    /// <summary>数据来源标识，便于诊断（§4.2）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("diagnosticCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiagnosticCode { get; set; }
}

/// <summary>响应体。</summary>
public sealed class PluginResponse
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = PluginProtocol.ProtocolVersion;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("readings")]
    public List<PluginReading> Readings { get; set; } = [];

    [JsonPropertyName("diagnostics")]
    public List<string> Diagnostics { get; set; } = [];

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginError? Error { get; set; }
}

/// <summary>错误体。</summary>
public sealed class PluginError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>插件错误码。</summary>
public static class PluginErrorCodes
{
    public const string DeviceNotConnected = "DEVICE_NOT_CONNECTED";
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string UnsupportedRequest = "UNSUPPORTED_REQUEST";
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string InternalError = "INTERNAL_ERROR";
}
