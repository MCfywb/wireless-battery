using System.Globalization;

namespace VivoTws3i.Protocol;

/// <summary>
/// vivo TWS 广播解析器。
/// </summary>
/// <remarks>
/// <para>
/// 偏移定义来自官方 vivo TWS App 的 <c>TwsParserV1</c> 与 <c>n2/e.java</c>
/// （经 jadx 反编译确认），本机实测逐值吻合，见 docs/protocols/vivo-tws-3i.md §13、§16。
/// </para>
/// <para>
/// <b>本实现只解析 TwsParserV1 格式</b>——即目标型号 vivo TWS 3i 实测通过的格式。
/// 其他格式（TWS V2、Advertise、单耳类）虽然偏移已知，但本型号没有实测样本支撑，
/// 解析器会明确拒绝并给出诊断，避免把未验证格式误读成有效读数。
/// </para>
/// <para>
/// 载荷偏移（相对 Windows 已剥离 CompanyId 后的 manufacturer data 起点）：
/// <code>
/// [0]      0x08        广播类型（TWS 对类）
/// [1]      0x01        格式版本 V1
/// [2..7]   MAC
/// [8]      状态字节：低 3 位在盒状态，高 2 位 mPairState 连接状态
/// [9]      充电盒电量 %
/// [10]     右耳电量 %
/// [11]     左耳电量 %
/// [12]     充电状态：bit0 左充 / bit1 右充 / bit2 盒充
/// [13]     型号 id
/// [14..17] 未知 4 字节
/// </code>
/// </para>
/// </remarks>
public static class VivoTwsAdvertisementParser
{
    /// <summary>CompanyId，Windows 的 ManufacturerData 已剥离此 2 字节。</summary>
    public const ushort CompanyId = 0x0837;

    /// <summary>广播类型：TWS 对类。</summary>
    public const byte BleTypeTws = 0x08;

    /// <summary>广播类型：单耳 / 运动耳机类。</summary>
    public const byte BleTypeSingle = 0x09;

    /// <summary>本插件支持的格式版本（V1）。</summary>
    public const byte SupportedFormatVersion = 0x01;

    /// <summary>V1 格式的最小载荷长度（偏移 0..17）。</summary>
    public const int TwsV1PayloadLength = 18;

    /// <summary>表示「设备未提供该部件读数」的字节值。</summary>
    public const byte NotReportedMarker = 0xFF;

    private const int OffsetBleType = 0;
    private const int OffsetFormatVersion = 1;
    private const int OffsetMac = 2;
    private const int OffsetStateByte = 8;
    private const int OffsetCaseBattery = 9;
    private const int OffsetRightBattery = 10;
    private const int OffsetLeftBattery = 11;
    private const int OffsetChargeState = 12;
    private const int OffsetModelId = 13;

    /// <summary>
    /// 解析一条广播载荷。
    /// </summary>
    /// <param name="payload">
    /// manufacturer data 载荷（不含 CompanyId）。长度必须不小于 <see cref="TwsV1PayloadLength"/>。
    /// </param>
    /// <param name="advertisement">解析成功时非 null。</param>
    /// <param name="status">解析状态。</param>
    /// <returns>仅当 <paramref name="status"/> 为 <see cref="VivoTwsParseStatus.Ok"/> 时返回 true。</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        out VivoTwsAdvertisement? advertisement,
        out VivoTwsParseStatus status)
    {
        advertisement = null;

        if (payload.Length == 0)
        {
            status = VivoTwsParseStatus.EmptyPayload;
            return false;
        }

        var bleType = payload[OffsetBleType];
        if (bleType != BleTypeTws && bleType != BleTypeSingle)
        {
            // 例如本机曾抓到 payload[0]=0x85 的其它 vivo 广播，不是 TWS 格式，必须拒绝。
            status = VivoTwsParseStatus.NotTwsAdvertisement;
            return false;
        }

        var version = payload[OffsetFormatVersion];
        if (version != SupportedFormatVersion)
        {
            status = VivoTwsParseStatus.UnsupportedVersion;
            return false;
        }

        if (bleType != BleTypeTws)
        {
            // 单耳 / 运动耳机类（0x09）：偏移体系不同，且本插件未实测，不解析。
            status = VivoTwsParseStatus.UnsupportedVersion;
            return false;
        }

        if (payload.Length < TwsV1PayloadLength)
        {
            status = VivoTwsParseStatus.TooShort;
            return false;
        }

        var diagnostics = new List<string>();

        var stateByte = payload[OffsetStateByte];
        var chargeState = payload[OffsetChargeState];

        var leftBattery = ReadBattery(payload[OffsetLeftBattery], "左耳", diagnostics);
        var rightBattery = ReadBattery(payload[OffsetRightBattery], "右耳", diagnostics);
        var caseBattery = ReadBattery(payload[OffsetCaseBattery], "充电盒", diagnostics);

        var pairState = (stateByte >> 3) & 0x03;

        advertisement = new VivoTwsAdvertisement
        {
            MacAddress = FormatMac(payload.Slice(OffsetMac, 6)),
            ModelId = payload[OffsetModelId],
            LeftEarbudBattery = leftBattery,
            RightEarbudBattery = rightBattery,
            ChargingCaseBattery = caseBattery,
            LeftEarbudCharging = ReadBit(chargeState, 0),
            RightEarbudCharging = ReadBit(chargeState, 1),
            ChargingCaseCharging = ReadBit(chargeState, 2),
            LeftEarbudInCase = ReadBit(stateByte, 0),
            RightEarbudInCase = ReadBit(stateByte, 1),
            BaseClosed = ReadBit(stateByte, 2),
            ConnectedState = (pairState & 0x02) == 0x02,
            PairState = pairState,
            Format = VivoTwsAdvertisementFormat.TwsV1,
            Diagnostics = diagnostics,
        };

        status = VivoTwsParseStatus.Ok;
        return true;
    }

    /// <summary>
    /// 读取一个电量字节。
    /// <list type="bullet">
    /// <item><description>0xFF：设备未提供 → null + <see cref="VivoTwsDiagnostics.ComponentNotReported"/>。</description></item>
    /// <item><description>大于 100：越界 → null + <see cref="VivoTwsDiagnostics.BatteryOutOfRange"/>，不钳制。</description></item>
    /// </list>
    /// </summary>
    private static int? ReadBattery(byte raw, string componentName, List<string> diagnostics)
    {
        if (raw == NotReportedMarker)
        {
            diagnostics.Add($"{VivoTwsDiagnostics.ComponentNotReported}:{componentName}");
            return null;
        }

        if (raw > 100)
        {
            diagnostics.Add($"{VivoTwsDiagnostics.BatteryOutOfRange}:{componentName}={raw}");
            return null;
        }

        return raw;
    }

    /// <summary>读取位域；格式不报告该位时返回 null。</summary>
    private static bool? ReadBit(byte value, int bit)
    {
        // 位 0..2 在 TWS V1 中恒有定义；bit3 起的充电位同理。返回可空以便将来扩展未定义位。
        return (value & (1 << bit)) != 0;
    }

    private static string FormatMac(ReadOnlySpan<byte> mac)
    {
        return string.Create(17, mac.ToArray(), (span, src) =>
        {
            const string hex = "0123456789ABCDEF";
            for (var i = 0; i < 6; i++)
            {
                if (i > 0)
                {
                    span[i * 3 - 1] = ':';
                }

                span[i * 3] = hex[src[i] >> 4];
                span[i * 3 + 1] = hex[src[i] & 0x0F];
            }
        });
    }

    /// <summary>把十六进制字符串（可含空格、冒号、连字符）解析为字节序列。</summary>
    public static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var cleaned = new string(hex.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray());
        if (cleaned.Length % 2 != 0)
        {
            return false;
        }

        var result = new byte[cleaned.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
            {
                return false;
            }
        }

        bytes = result;
        return true;
    }
}
