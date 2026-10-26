using System.Text.Json.Serialization;

namespace VivoTws3i.Protocol;

/// <summary>
/// 插件 profile：型号识别与匹配规则。
/// </summary>
/// <remarks>
/// 依据官方 App 的 <c>assets/tws_config.json</c>：vivo TWS 3i = model <b>132 / 133</b>，
/// project <c>DPD2407A</c>。同系列长续航版为 model <b>144 / 145</b>（<c>DPD2407AC</c>），
/// 属不同型号，按「每型号独立验证」原则，**在未取得实测样本前不纳入支持列表**。
/// </remarks>
public sealed class VivoTws3iProfile
{
    /// <summary>插件 id，须与 manifest 的 id 一致。</summary>
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = "vivo-tws-3i";

    /// <summary>Fast Pair CompanyId（十进制；0x0837 = 2103）。</summary>
    [JsonPropertyName("companyId")]
    public int CompanyId { get; set; } = 0x0837;

    /// <summary>已实测通过的广播格式。</summary>
    [JsonPropertyName("supportedFormats")]
    public List<string> SupportedFormats { get; set; } = ["tws-v1"];

    /// <summary>支持列表内的型号 id。</summary>
    [JsonPropertyName("modelIds")]
    public List<int> ModelIds { get; set; } = [132, 133];

    /// <summary>型号 id → 产品名（仅用于展示）。</summary>
    [JsonPropertyName("modelNames")]
    public Dictionary<int, string> ModelNames { get; set; } = new()
    {
        [132] = "vivo TWS 3i",
        [133] = "vivo TWS 3i",
    };

    /// <summary>同系列但未实测的型号 id；遇到时只记录，不解析为该型号。</summary>
    [JsonPropertyName("knownButUnverifiedModelIds")]
    public List<int> KnownButUnverifiedModelIds { get; set; } = [144, 145];

    /// <summary>对应工程代号。</summary>
    [JsonPropertyName("projectNames")]
    public List<string> ProjectNames { get; set; } = ["DPD2407A"];

    /// <summary>蓝牙名前缀，仅作辅助提示，**不作为身份判定依据**。</summary>
    [JsonPropertyName("bluetoothNamePrefixes")]
    public List<string> BluetoothNamePrefixes { get; set; } = ["vivo TWS 3i"];
}

/// <summary>型号匹配判定结果。</summary>
public enum ModelMatchVerdict
{
    /// <summary>在支持列表内。</summary>
    Supported,

    /// <summary>已知存在但未独立验证。</summary>
    KnownButUnverified,

    /// <summary>未知型号。</summary>
    Unknown,
}

/// <summary>
/// 型号匹配器：以广播中的 model id 为身份依据，不以产品名匹配（§6 陷阱）。
/// </summary>
public sealed class VivoTws3iModelMatcher
{
    private readonly VivoTws3iProfile _profile;

    public VivoTws3iModelMatcher(VivoTws3iProfile profile)
    {
        _profile = profile;
    }

    public VivoTws3iProfile Profile => _profile;

    /// <summary>判定型号。</summary>
    public ModelMatchVerdict Evaluate(int modelId)
    {
        if (_profile.ModelIds.Contains(modelId))
        {
            return ModelMatchVerdict.Supported;
        }

        if (_profile.KnownButUnverifiedModelIds.Contains(modelId))
        {
            return ModelMatchVerdict.KnownButUnverified;
        }

        return ModelMatchVerdict.Unknown;
    }

    /// <summary>该广播是否属于本插件支持的型号。</summary>
    public bool IsSupported(int modelId) => Evaluate(modelId) == ModelMatchVerdict.Supported;

    /// <summary>取型号展示名；未知时回退为通用描述。</summary>
    public string GetDisplayName(int modelId)
    {
        return _profile.ModelNames.TryGetValue(modelId, out var name)
            ? name
            : $"vivo TWS (model {modelId})";
    }
}
