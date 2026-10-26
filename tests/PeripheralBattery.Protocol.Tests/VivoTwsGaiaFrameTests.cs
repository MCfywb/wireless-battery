using VivoTws3i.Protocol;
using Xunit;

namespace PeripheralBattery.Protocol.Tests;

/// <summary>
/// GAIA 帧测试：构造 / 解析 / 电量解码。
/// </summary>
/// <remarks>
/// 全部为离线测试，不需要真机：夹具取自 2026-10-27 主动通道实验的实测帧
/// （<c>testdata/active-channel-20261027.csv</c>）。
/// </remarks>
public sealed class VivoTwsGaiaFrameTests
{
    private static byte[] Hex(string text) => Convert.FromHexString(text);

    [Fact]
    public void 构造的握手帧必须与上游一致()
    {
        // 本机与 DeviceProbe 实际发出的握手帧完全一致。
        var frame = VivoTwsGaiaFrameReader.Build(
            version: 4, vendor: VivoTwsGaiaFrameReader.VendorGaia,
            command: VivoTwsGaiaFrameReader.CommandHandshake, payload: []);

        Assert.Equal("FF040000000A0300", Convert.ToHexString(frame));
    }

    [Fact]
    public void 应能解析实测的握手响应()
    {
        // 2026-10-27 实测：FF030004000A830000030301 → GAIA 3.3.1
        var frames = VivoTwsGaiaFrameReader.Parse(Hex("FF030004000A830000030301"));

        var frame = Assert.Single(frames);
        Assert.Equal(3, frame.Version);
        Assert.Equal(VivoTwsGaiaFrameReader.VendorGaia, frame.Vendor);
        Assert.Equal(VivoTwsGaiaFrameReader.CommandHandshakeAck, frame.Command);
        Assert.Equal("00030301", Convert.ToHexString(frame.Payload));
    }

    [Theory]
    // 实测三形态：仓不可测 / 全可测 / 右耳在盒内不可测
    [InlineData("002822FF00", 40, 34, null)]
    [InlineData("0028226000", 40, 34, 96)]
    [InlineData("0028FF6000", 40, null, 96)]
    public void 电量帧应按实测形态解码(string payload, int? left, int? right, int? casePct)
    {
        var battery = VivoTwsGaiaFrameReader.DecodeBattery(Hex(payload));

        Assert.NotNull(battery);
        Assert.Equal(left, battery.Left);
        Assert.Equal(right, battery.Right);
        Assert.Equal(casePct, battery.Case);
    }

    [Fact]
    public void 未知部件必须是null而不是0()
    {
        // 0xFF 表示「该部件当前不可测」，按 §4.2 必须落成 null，绝不能当 0%。
        var battery = VivoTwsGaiaFrameReader.DecodeBattery(Hex("0028FF6000"));

        Assert.NotNull(battery);
        Assert.Null(battery.Right);
        Assert.NotEqual(0, battery.Right);
    }

    [Fact]
    public void 越界电量必须按未知处理且不钳制()
    {
        // 0x65 = 101 > 100：不得钳制成 100，应判为未知。
        var battery = VivoTwsGaiaFrameReader.DecodeBattery(Hex("0065226000"));

        Assert.NotNull(battery);
        Assert.Null(battery.Left);
    }

    [Theory]
    // 2026-10-27 控制变量：未插电 = 0x00；插上充电线 = 0x04（bit2=盒在充电）
    [InlineData("004C4EFF00", false)]
    [InlineData("004C4E5204", true)]
    [InlineData("004C4EFF04", true)]
    [InlineData("004C4E5200", false)]
    public void 盒充电位应与实测一致(string payload, bool expected)
    {
        var battery = VivoTwsGaiaFrameReader.DecodeBattery(Hex(payload));

        Assert.NotNull(battery);
        Assert.Equal(expected, battery.CaseCharging);
    }

    [Fact]
    public void 左右耳充电位尚未验证不得暴露()
    {
        // bit0/bit1 语义未验证（实验中耳机从未进入充电态），故一律为未知。
        var battery = VivoTwsGaiaFrameReader.DecodeBattery(Hex("004C4E5204"));

        Assert.NotNull(battery);
        Assert.Null(battery.EarbudsCharging);
    }

    [Fact]
    public void 载荷不足时应返回null()
    {
        Assert.Null(VivoTwsGaiaFrameReader.DecodeBattery(Hex("002822")));
    }

    [Fact]
    public void 应能从连续字节流解析出多帧()
    {
        // 一次收到两帧：注册 ACK + 电量上报。
        var stream = Hex("FF030001001B82040000" + "FF030005001B8207002822FF00");

        var frames = VivoTwsGaiaFrameReader.Parse(stream);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x8204, frames[0].Command);
        Assert.Equal(VivoTwsGaiaFrameReader.CommandBatteryReport, frames[1].Command);
    }

    [Fact]
    public void 半帧必须被丢弃而不是误解析()
    {
        // 末帧长度域声明 5 字节但只到了 2 字节：不得产出该帧。
        var frames = VivoTwsGaiaFrameReader.Parse(Hex("FF030005001B8207002822"));

        Assert.Empty(frames);
    }

    [Fact]
    public void 构造与解析应可往返()
    {
        var payload = Hex("002822FF00");
        var frame = VivoTwsGaiaFrameReader.Build(
            3, VivoTwsGaiaFrameReader.VendorVivo,
            VivoTwsGaiaFrameReader.CommandBatteryReport, payload);

        var parsed = VivoTwsGaiaFrameReader.Parse(frame);

        var single = Assert.Single(parsed);
        Assert.Equal(VivoTwsGaiaFrameReader.CommandBatteryReport, single.Command);
        Assert.Equal(payload, single.Payload);
    }
}
