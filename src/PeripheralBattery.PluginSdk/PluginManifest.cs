using System.Text.Json.Serialization;

namespace PeripheralBattery.PluginSdk;

/// <summary>插件 manifest。字段定义取自 docs/development-plan.md §5.2。</summary>
public sealed class PluginManifest
{
    /// <summary>当前支持的 manifest 结构版本。</summary>
    public const int CurrentManifestVersion = 1;

    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; set; } = CurrentManifestVersion;

    /// <summary>插件 id：仅允许小写字母、数字与连字符。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("minHostVersion")]
    public string MinHostVersion { get; set; } = string.Empty;

    /// <summary>Worker 可执行文件相对插件目录的路径。</summary>
    [JsonPropertyName("worker")]
    public string Worker { get; set; } = string.Empty;

    [JsonPropertyName("transports")]
    public List<string> Transports { get; set; } = [];

    [JsonPropertyName("deviceMatchers")]
    public DeviceMatchers DeviceMatchers { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public PluginCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("verification")]
    public VerificationInfo Verification { get; set; } = new();
}

/// <summary>设备匹配规则。注意：产品名只能作为辅助，身份判定须靠型号 id 等硬依据。</summary>
public sealed class DeviceMatchers
{
    [JsonPropertyName("bluetoothNamePrefixes")]
    public List<string> BluetoothNamePrefixes { get; set; } = [];

    [JsonPropertyName("serviceUuids")]
    public List<string> ServiceUuids { get; set; } = [];

    [JsonPropertyName("usbVidPid")]
    public List<string> UsbVidPid { get; set; } = [];

    /// <summary>型号 id 白名单（厂商广播中携带的 model id）。这是主要身份依据。</summary>
    [JsonPropertyName("modelIds")]
    public List<int> ModelIds { get; set; } = [];
}

/// <summary>插件能力声明。</summary>
public sealed class PluginCapabilities
{
    [JsonPropertyName("batteryComponents")]
    public List<string> BatteryComponents { get; set; } = [];

    /// <summary>首期设备插件一律声明只读（§5.2）。</summary>
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; } = true;

    [JsonPropertyName("requiresAdministrator")]
    public bool RequiresAdministrator { get; set; }

    [JsonPropertyName("usesNetwork")]
    public bool UsesNetwork { get; set; }

    /// <summary>
    /// 插件是否在读数中给出<b>在线判定</b>（<c>present</c> 字段）。
    /// </summary>
    /// <remarks>
    /// <c>true</c> 时宿主会在判定离线后<b>移除</b>该设备条目；
    /// 不声明（保持 <c>false</c>）则维持原有「只增不减」行为，保证向后兼容。
    /// 在线判定只读系统已维护的蓝牙状态，不发起连接、不写设备，
    /// 与 <see cref="ReadOnly"/> 不冲突。
    /// </remarks>
    [JsonPropertyName("reportsPresence")]
    public bool ReportsPresence { get; set; }

    /// <summary>
    /// 插件是否维持<b>常驻连接</b>（宿主无需干预，插件自己在后台建链并保持）。
    /// </summary>
    /// <remarks>
    /// 为 <c>true</c> 时，插件会在读数中持续给出设备自发推送的数值，
    /// 宿主应正常展示（即实时更新），详见 D-012。
    /// 常驻连接仍属<b>只读</b>：只接收设备主动上报，不下发状态变更类命令。
    /// </remarks>
    [JsonPropertyName("residentConnection")]
    public bool ResidentConnection { get; set; }

    /// <summary>
    /// 是否支持按需主动读取（<see cref="PluginSdk.PluginMethods.ActiveRead"/>）。
    /// </summary>
    /// <remarks>按 D-010：宿主只在用户显式点击「立即刷新」时调用。</remarks>
    [JsonPropertyName("activeRead")]
    public bool ActiveRead { get; set; }
}

/// <summary>验证状态。枚举见 docs/development-plan.md §13.4。</summary>
public sealed class VerificationInfo
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = VerificationStatuses.Unknown;

    [JsonPropertyName("testedModels")]
    public List<string> TestedModels { get; set; } = [];

    [JsonPropertyName("testedFirmware")]
    public List<string> TestedFirmware { get; set; } = [];

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}

/// <summary>验证状态常量。</summary>
public static class VerificationStatuses
{
    public const string Unknown = "unknown";
    public const string Candidate = "candidate";
    public const string Captured = "captured";
    public const string ProtocolVerified = "protocol-verified";
    public const string HardwareVerified = "hardware-verified";

    /// <summary>全部合法取值，用于 manifest 校验。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Unknown,
        Candidate,
        Captured,
        ProtocolVerified,
        HardwareVerified,
    ];
}
