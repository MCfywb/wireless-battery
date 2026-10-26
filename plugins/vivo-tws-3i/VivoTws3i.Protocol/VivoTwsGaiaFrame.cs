namespace VivoTws3i.Protocol;

/// <summary>
/// GAIA 单帧。
/// </summary>
/// <remarks>
/// 本机实测于 vivo TWS 3i 的私有通道（RFCOMM <c>00000837-d102-11e1-9b23-00025b00a5a5</c>，
/// 及 BLE 侧 <c>00001100-d102-…</c>），见 docs/protocols/vivo-tws-3i.md §18、§21。
/// </remarks>
/// <param name="Version">协议版本；本机握手响应为 3（GAIA 3.3.1）。</param>
/// <param name="Flags">帧标志：<c>0x01</c> 校验和、<c>0x02</c> 长度扩展。</param>
/// <param name="Vendor">厂商域，如 <c>0x000A</c>（GAIA）、<c>0x001B</c>。</param>
/// <param name="Command">命令字，如 <c>0x0300</c> 握手、<c>0x8207</c> 电量上报。</param>
/// <param name="Payload">载荷。</param>
public sealed record VivoTwsGaiaFrame(
    int Version,
    int Flags,
    int Vendor,
    int Command,
    byte[] Payload);

/// <summary>
/// GAIA 帧的构造与解析。
/// </summary>
/// <remarks>
/// <para>
/// 帧结构（与上游 <c>frame()</c> 对齐，本机收发往返实测一致）：
/// <code>
/// [0]     0xFF       帧头
/// [1]     version
/// [2]     flags
/// [3]     length     （flags 含 0x02 时为 [3..4] 两字节）
/// [n..n+1] vendor    大端
/// [n+2..n+3] command 大端
/// [...]   payload
/// </code>
/// </para>
/// <para>
/// 本类<b>纯解析，不做任何 IO</b>，因而可以脱离设备离线单测。
/// </para>
/// </remarks>
public static class VivoTwsGaiaFrameReader
{
    /// <summary>帧头。</summary>
    public const byte FrameStart = 0xFF;

    /// <summary>长度扩展标志。</summary>
    public const int FlagLengthExtension = 0x02;

    /// <summary>电量上报命令（设备 → 主机，自发推送）。</summary>
    public const int CommandBatteryReport = 0x8207;

    /// <summary>握手命令（主机 → 设备）。</summary>
    public const int CommandHandshake = 0x0300;

    /// <summary>握手响应（设备 → 主机）。</summary>
    public const int CommandHandshakeAck = 0x8300;

    /// <summary>GAIA 厂商域。</summary>
    public const int VendorGaia = 0x000A;

    /// <summary>vivo 私有电量/埋点域。</summary>
    public const int VendorVivo = 0x001B;

    /// <summary>构造一帧。</summary>
    /// <param name="version">协议版本。</param>
    /// <param name="vendor">厂商域。</param>
    /// <param name="command">命令字。</param>
    /// <param name="payload">载荷。</param>
    /// <param name="flags">帧标志。</param>
    public static byte[] Build(int version, int vendor, int command, byte[] payload, int flags = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var extended = (flags & FlagLengthExtension) != 0;
        var headerSize = extended ? 5 : 4;
        var buffer = new byte[headerSize + 4 + payload.Length];

        buffer[0] = FrameStart;
        buffer[1] = (byte)version;
        buffer[2] = (byte)flags;

        if (extended)
        {
            buffer[3] = (byte)((payload.Length >> 8) & 0xFF);
            buffer[4] = (byte)(payload.Length & 0xFF);
        }
        else
        {
            buffer[3] = (byte)payload.Length;
        }

        buffer[headerSize] = (byte)((vendor >> 8) & 0xFF);
        buffer[headerSize + 1] = (byte)(vendor & 0xFF);
        buffer[headerSize + 2] = (byte)((command >> 8) & 0xFF);
        buffer[headerSize + 3] = (byte)(command & 0xFF);

        Array.Copy(payload, 0, buffer, headerSize + 4, payload.Length);
        return buffer;
    }

    /// <summary>
    /// 从缓冲区解析出尽可能多的完整帧（容忍尾部半帧）。
    /// </summary>
    /// <param name="data">缓冲区；可含多帧，也可含不完整的尾帧。</param>
    /// <returns>解析出的完整帧列表。</returns>
    /// <remarks>
    /// 遇到非 <c>0xFF</c> 起始的字节就地跳过（用于跨帧重新同步）。
    /// 未构成完整帧的尾部字节被丢弃，由调用方保留在接收缓冲中等待续传。
    /// </remarks>
    public static List<VivoTwsGaiaFrame> Parse(ReadOnlySpan<byte> data)
    {
        var frames = new List<VivoTwsGaiaFrame>();
        var i = 0;

        while (i + 4 <= data.Length)
        {
            if (data[i] != FrameStart)
            {
                i++;
                continue;
            }

            var version = data[i + 1];
            var flags = data[i + 2];

            int length;
            int vendorIndex;
            if ((flags & FlagLengthExtension) != 0)
            {
                if (i + 5 > data.Length)
                {
                    break;
                }

                length = (data[i + 3] << 8) | data[i + 4];
                vendorIndex = i + 5;
            }
            else
            {
                length = data[i + 3];
                vendorIndex = i + 4;
            }

            if (vendorIndex + 4 > data.Length)
            {
                break;
            }

            var vendor = (data[vendorIndex] << 8) | data[vendorIndex + 1];
            var command = (data[vendorIndex + 2] << 8) | data[vendorIndex + 3];

            var start = vendorIndex + 4;
            if (start + length > data.Length)
            {
                break;
            }

            frames.Add(new VivoTwsGaiaFrame(
                version, flags, vendor, command, data.Slice(start, length).ToArray()));

            i = start + length;
        }

        return frames;
    }

    /// <summary>
    /// 电量上报帧 <c>0x8207</c> 的三部件解码结果。
    /// </summary>
    /// <param name="Left">左耳电量；<c>null</c> 表示未知（上报 <c>0xFF</c>）。</param>
    /// <param name="Right">右耳电量；<c>null</c> 表示未知。</param>
    /// <param name="Case">充电盒电量；<c>null</c> 表示未知。</param>
    /// <param name="ChargeBits">充电状态位原始值；<c>null</c> 表示未提供。</param>
    public sealed record BatteryReport(int? Left, int? Right, int? Case, int? ChargeBits)
    {
        /// <summary>位掩码：盒在充电。</summary>
        public const int MaskCaseCharging = 0x04;

        /// <summary>
        /// 盒是否在充电（充电位 bit2）。
        /// </summary>
        /// <remarks>
        /// <b>这是唯一经过验证的充电位</b>：2026-10-27 控制变量实验中，
        /// 该位与「给充电盒插/拔充电线」同步翻转，且与同一时刻<b>广播路径</b>的
        /// <c>CaseChg</c> 一致（双通道交叉验证），见 docs/protocols/vivo-tws-3i.md §22。
        /// </remarks>
        public bool? CaseCharging => ChargeBits is { } bits ? (bits & MaskCaseCharging) != 0 : null;

        /// <summary>
        /// 左右耳是否在充电 —— <b>不提供</b>。
        /// </summary>
        /// <remarks>
        /// bit0（左）/ bit1（右）的语义<b>尚未验证</b>：控制变量实验中耳机始终未进入充电态
        /// （盒盖须保持打开才能维持蓝牙连接，而开盖时本型号的盒不给耳充电），
        /// 广播与 GAIA 两条通道的左右耳充电位全程均为 0，无从判定。
        /// 按「不得断言未验证语义」原则，不暴露该字段，调用方应显示未知。
        /// </remarks>
        public bool? EarbudsCharging => null;
    }

    /// <summary>
    /// 把 <c>0x8207</c> 帧的载荷解码为三部件电量。
    /// </summary>
    /// <param name="payload">帧载荷，形态为 <c>[ACK][左][右][仓][充电位]</c>。</param>
    /// <returns>解码结果；载荷长度不足或不是电量帧时返回 <c>null</c>。</returns>
    /// <remarks>
    /// <para><c>0xFF</c> 表示该部件当前<b>不可测</b>（典型情况是该耳在盒内），
    /// 解码为 <c>null</c>（未知）而非 <c>0%</c>——严格区分「未知」与「真实 0」（§4.2）。</para>
    /// <para>越界值（&gt;100 且非 0xFF）按未知处理，不钳制。</para>
    /// </remarks>
    public static BatteryReport? DecodeBattery(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length < 5)
        {
            return null;
        }

        return new BatteryReport(
            Normalize(payload[1]),
            Normalize(payload[2]),
            Normalize(payload[3]),
            payload[4]);
    }

    private static int? Normalize(byte value)
        => value == 0xFF || value > 100 ? null : value;
}
