using System.Text;
using System.Text.Json;

namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>从 <c>0x8224</c> 遥测帧中提取的样本。</summary>
/// <param name="AudioLink">音频链路状态：<c>M_A2dp</c>（音乐 A2DP 链路）、<c>M_Sco</c>（SCO/HFP 链路）、<c>rms_mute</c>（静音标记）。</param>
/// <param name="Firmware">固件版本（根节点 <c>/V</c>）。</param>
/// <param name="BatteryCsv">电量（根节点 <c>/C</c>），形如 <c>93_90</c>（左_右）。</param>
/// <param name="Raw">解析出的可读字段子集。</param>
internal sealed record TelemetrySample(
    (string A2dp, string Sco, string Mute) AudioLink,
    string? Firmware,
    string? BatteryCsv,
    Dictionary<string, string> Raw);

/// <summary>
/// <c>0x8224</c> 遥测帧解析。
/// </summary>
/// <remarks>
/// <para>该帧的载荷是一段 JSON（前后带若干字节头），内容为耳机自报的状态。
/// 2026-10-27 首次解析出的结构（<c>--dump-telemetry</c> 落地原文核对）：</para>
/// <code>
/// {
///   "eventId": "A102|10156",
///   "params": {
///     "rms_mute": "0", "P": "4_4", "P_L": "4", "P_R": "4",
///     "B": "1_1", "B_H": "1", "B_P": "1",
///     "A": "0_0", "A_M": "0", "A_L": "0",
///     "RSSI": "-68_0_-76", "RSSI_1": "-68", "RSSI_2": "0", "RSSI_P": "-76",
///     "M_Sco": "0", "M_A2dp": "1"
///   },
///   "V": "2.19.7_2.19.7",
///   "C": "93_90",
///   "P": "4_4", "B": "1_1", "D": "0"
/// }
/// </code>
/// <para>其中三个字段对本项目最关键：</para>
/// <list type="bullet">
/// <item><description><c>M_A2dp</c> / <c>M_Sco</c>：耳机视角的音频链路状态，
/// 是 T1「常驻 RFCOMM 会不会影响听歌」的第一手证据（来自耳机本身，比 Windows 侧端点快照更贴近）。</description></item>
/// <item><description><c>/C</c>：电量，形如 <c>93_90</c>（左_右），
/// 与 <c>0x8207</c> 电量帧<b>逐值一致</b>，构成第二个独立电量来源，可交叉验证。</description></item>
/// <item><description><c>/V</c>：固件版本。</description></item>
/// </list>
/// <para>载荷里<b>存在重复键</b>（<c>P</c>、<c>B</c> 等在根与 params 中各出现一次），
/// 故用 <see cref="JsonDocument"/> 解析（取最后一次出现），不用反序列化成字典
/// （后者遇重复键会抛异常）。</para>
/// </remarks>
internal static class TelemetryParser
{
    /// <summary>值得记录到报告里的字段（其余字段不进入报告，避免噪声淹没关键项）。</summary>
    private static readonly string[] InterestingFields =
    [
        // 音频链路（干扰判定的核心）
        "M_A2dp", "M_Sco", "rms_mute",
        // 电量与充电
        "C", "P", "P_L", "P_R", "B", "B_H", "B_P", "A", "A_M", "A_L", "D",
        // 射频
        "RSSI", "RSSI_1", "RSSI_2", "RSSI_P",
        // 版本
        "V",
    ];

    /// <summary>尝试解析遥测载荷。</summary>
    public static bool TryParse(byte[] payload, out TelemetrySample? sample)
        => TryParse(payload, out sample, out _);

    /// <summary>尝试解析遥测载荷，并输出原始 JSON 文本。</summary>
    public static bool TryParse(byte[] payload, out TelemetrySample? sample, out string? json)
    {
        sample = null;
        json = null;

        // 载荷前面是若干字节头、结尾可能有填充字节，JSON 夹在中间。
        // 取第一个 '{' 到最后一个 '}' 之间的部分，避免把尾部填充喂给解析器。
        var start = Array.IndexOf(payload, (byte)'{');
        var end = Array.LastIndexOf(payload, (byte)'}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(payload, start, end - start + 1);

        try
        {
            using var document = JsonDocument.Parse(text);
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);

            Collect(document.RootElement, raw);

            if (raw.Count == 0)
            {
                return false;
            }

            json = text;
            sample = new TelemetrySample(
                (
                    raw.GetValueOrDefault("M_A2dp", "?"),
                    raw.GetValueOrDefault("M_Sco", "?"),
                    raw.GetValueOrDefault("rms_mute", "?")),
                raw.GetValueOrDefault("V"),
                raw.GetValueOrDefault("C"),
                raw);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>递归收集叶子字段（遥测 JSON 的 params 是嵌套对象）。</summary>
    private static void Collect(JsonElement element, Dictionary<string, string> raw)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Collect(property.Value, raw);
                continue;
            }

            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                continue;
            }

            if (!InterestingFields.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            raw[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }
    }
}
