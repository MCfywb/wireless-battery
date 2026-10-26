namespace VivoTws3i.Protocol;

/// <summary>
/// 设备标识构造：保证**主动通道与被动广播产出同一个 deviceId**。
/// </summary>
/// <remarks>
/// <para>两条通道拿到的地址形态不同，必须归一化后才能归并到同一台设备：</para>
/// <list type="bullet">
/// <item><description>被动广播：从 manufacturer data 的 MAC 字段解析，形如 <c>1C:11:2F:27:89:7A</c></description></item>
/// <item><description>主动 RFCOMM：<c>StreamSocket.Information.RemoteAddress.CanonicalName</c>，实测形如 <c>(1C:11:2F:27:89:7A)</c>——**带括号**</description></item>
/// </list>
/// <para>历史缺陷：早期实现只做「按冒号切分 + 转大写」，括号被保留，
/// 导致主动路径产出 <c>bt:(1C:11:2F:27:89:7A)</c>，与被动路径的
/// <c>bt:1C:11:2F:27:89:7A</c> 字符串不相等，宿主遂把同一台耳机显示成两台。</para>
/// </remarks>
public static class VivoTwsDeviceId
{
    /// <summary>设备标识前缀。</summary>
    public const string Prefix = "bt:";

    /// <summary>蓝牙地址十六进制字符数（6 字节）。</summary>
    private const int HexLength = 12;

    /// <summary>地址字节数。</summary>
    private const int AddressBytes = 6;

    /// <summary>
    /// 把任意形态的蓝牙地址规范化为与被动路径一致的 <c>XX:XX:XX:XX:XX:XX</c>。
    /// </summary>
    /// <param name="host">原始地址，允许含括号、空格、大小写混用。</param>
    /// <returns>大写、以冒号分隔的地址；无法识别时返回 <c>"unknown"</c>。</returns>
    public static string NormalizeAddress(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "unknown";
        }

        // 只保留十六进制字符：同时吃掉括号、空格与分隔符差异。
        var hex = new string(host.Where(char.IsAsciiHexDigit).ToArray());
        if (hex.Length != HexLength)
        {
            return "unknown";
        }

        var parts = new string[AddressBytes];
        for (var i = 0; i < AddressBytes; i++)
        {
            parts[i] = hex.Substring(i * 2, 2).ToUpperInvariant();
        }

        return string.Join(':', parts);
    }

    /// <summary>构造完整设备标识（<c>bt:XX:…</c>）。</summary>
    /// <param name="host">原始蓝牙地址。</param>
    public static string FromAddress(string? host) => Prefix + NormalizeAddress(host);
}
