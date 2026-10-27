namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>解析出的一帧 GAIA 报文。</summary>
/// <param name="Version">协议版本号（握手用 4，电量帧实测为 3）。</param>
/// <param name="Flags">标志位；bit1 置位表示长度字段为 2 字节扩展格式。</param>
/// <param name="Vendor">厂商标识：<c>0x000A</c> = GAIA，<c>0x001B</c> = vivo 私有。</param>
/// <param name="Command">命令字。</param>
/// <param name="Payload">载荷。</param>
internal readonly record struct GaiaFrame(int Version, int Flags, int Vendor, int Command, byte[] Payload);

/// <summary>
/// GAIA 帧编解码。
/// </summary>
/// <remarks>
/// <para>本探针<b>刻意不复用</b>产品代码 <c>VivoTws3i.Protocol</c> 里的实现，而是自带一份：
/// 隔离实验工具应保持独立实现，否则产品代码的 bug 会同时污染实验结论。</para>
/// <para>帧结构（<c>docs/protocols/vivo-tws-3i.md</c> §21）：
/// <c>FF | ver | flags | len | vendor(2) | cmd(2) | payload</c>；
/// <c>flags</c> 的 bit1 置位时 <c>len</c> 占 2 字节（大端）。</para>
/// </remarks>
internal static class GaiaFrameCodec
{
    /// <summary>帧头固定字节 <c>0xFF</c>。</summary>
    public const byte FrameMarker = 0xFF;

    /// <summary>长度扩展标志位：置位时长度字段占 2 字节。</summary>
    public const int FlagLengthExtension = 0x02;

    /// <summary>构造一帧 GAIA 报文。</summary>
    public static byte[] Build(int version, int vendor, int command, byte[] payload, int flags = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var extended = (flags & FlagLengthExtension) != 0;
        var headerSize = extended ? 5 : 4;
        var buffer = new byte[headerSize + 4 + payload.Length];

        buffer[0] = FrameMarker;
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
    /// 从缓冲区解析完整帧。
    /// </summary>
    /// <param name="buffer">字节缓冲；未组成完整帧的尾巴会被保留。</param>
    /// <returns>解析出的帧，以及本次可安全消费的字节数。</returns>
    public static (int Consumed, List<GaiaFrame> Frames) ParseAndConsume(List<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var frames = new List<GaiaFrame>();
        var index = 0;

        while (index + 4 <= buffer.Count)
        {
            if (buffer[index] != FrameMarker)
            {
                index++;
                continue;
            }

            var version = buffer[index + 1];
            var flags = buffer[index + 2];
            var headerSize = (flags & FlagLengthExtension) != 0 ? 5 : 4;

            if (index + headerSize + 4 > buffer.Count)
            {
                break;
            }

            var length = (flags & FlagLengthExtension) != 0
                ? (buffer[index + 3] << 8) | buffer[index + 4]
                : buffer[index + 3];

            var vendorIndex = index + headerSize;
            var vendor = (buffer[vendorIndex] << 8) | buffer[vendorIndex + 1];
            var command = (buffer[vendorIndex + 2] << 8) | buffer[vendorIndex + 3];
            var payloadStart = vendorIndex + 4;

            if (payloadStart + length > buffer.Count)
            {
                break;
            }

            frames.Add(new GaiaFrame(
                version,
                flags,
                vendor,
                command,
                buffer.GetRange(payloadStart, length).ToArray()));

            index = payloadStart + length;
        }

        return (index, frames);
    }
}
