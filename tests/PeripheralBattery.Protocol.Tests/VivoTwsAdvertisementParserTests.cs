using VivoTws3i.Protocol;
using Xunit;

namespace PeripheralBattery.Protocol.Tests;

/// <summary>
/// 广播解析器测试。夹具取自 plugins/vivo-tws-3i/tests/fixtures/。
/// </summary>
public class VivoTwsAdvertisementParserTests
{
    [Fact]
    public void 正常帧_解析出三部件电量()
    {
        var payload = FixtureLoader.Load("tws-v1-normal.hex");

        var ok = VivoTwsAdvertisementParser.TryParse(payload, out var adv, out var status);

        Assert.True(ok);
        Assert.Equal(VivoTwsParseStatus.Ok, status);
        Assert.NotNull(adv);
        Assert.Equal(100, adv!.ChargingCaseBattery);
        Assert.Equal(83, adv.RightEarbudBattery);
        Assert.Equal(92, adv.LeftEarbudBattery);
        Assert.Equal(133, adv.ModelId);
        Assert.Equal(VivoTwsAdvertisementFormat.TwsV1, adv.Format);
    }

    [Fact]
    public void 正常帧_解析出充电位与在盒状态()
    {
        var payload = FixtureLoader.Load("tws-v1-normal.hex");

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.NotNull(adv);
        // 充电状态字节 = 0x04 → bit2 置位：充电盒在充电，左右耳未充电
        Assert.True(adv!.ChargingCaseCharging);
        Assert.False(adv.LeftEarbudCharging);
        Assert.False(adv.RightEarbudCharging);
        // 状态字节 = 0x13 = 0b0001_0011
        //   bit0/bit1 = 1 → 两只耳都在盒
        //   bit2     = 0 → 基座未闭合（盒盖打开）
        //   bit3-4   = 10 → mPairState = 2（已连接）
        Assert.True(adv.LeftEarbudInCase);
        Assert.True(adv.RightEarbudInCase);
        Assert.False(adv.BaseClosed);
    }

    [Fact]
    public void 基座闭合时_状态字节bit2置位()
    {
        // 0x17 = 0b0001_0111：两耳在盒 + 基座闭合 + 已连接
        var payload = FixtureLoader.Load("tws-v1-normal.hex").ToArray();
        payload[8] = 0x17;

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.NotNull(adv);
        Assert.True(adv!.BaseClosed);
        Assert.True(adv.LeftEarbudInCase);
        Assert.True(adv.RightEarbudInCase);
        Assert.True(adv.ConnectedState);
    }

    [Fact]
    public void 正常帧_连接状态位来自状态字节高两位()
    {
        var payload = FixtureLoader.Load("tws-v1-normal.hex");

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.NotNull(adv);
        // 0x13 >> 3 & 3 = 2 → bit1 置位 → 已连接
        Assert.Equal(2, adv!.PairState);
        Assert.True(adv.ConnectedState);
    }

    [Fact]
    public void 未连接帧_连接状态为false()
    {
        var payload = FixtureLoader.Load("tws-v1-disconnected.hex");

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.NotNull(adv);
        // 0x03 >> 3 & 3 = 0 → 未连接
        Assert.Equal(0, adv!.PairState);
        Assert.False(adv.ConnectedState);
        // 电量仍应正常读出——连接状态与电量位物理隔离
        Assert.Equal(92, adv.LeftEarbudBattery);
        Assert.Equal(83, adv.RightEarbudBattery);
    }

    [Fact]
    public void 单耳在盒_位域正确区分()
    {
        var payload = FixtureLoader.Load("tws-v1-one-ear-in-case.hex");

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.NotNull(adv);
        Assert.True(adv!.LeftEarbudInCase);
        Assert.False(adv.RightEarbudInCase);
        Assert.False(adv.BaseClosed);
    }

    [Fact]
    public void 部件未提供_必须为null且带诊断_不得显示为0()
    {
        var payload = FixtureLoader.Load("tws-v1-case-unreported.hex");

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.NotNull(adv);
        Assert.Null(adv!.ChargingCaseBattery);
        Assert.Contains(adv.Diagnostics, d => d.StartsWith(VivoTwsDiagnostics.ComponentNotReported, StringComparison.Ordinal));
        // 其余部件不受影响
        Assert.Equal(92, adv.LeftEarbudBattery);
        Assert.Equal(83, adv.RightEarbudBattery);
    }

    [Fact]
    public void 电量越界_必须为null且记录诊断_不得钳制()
    {
        var payload = FixtureLoader.Load("tws-v1-out-of-range.hex");

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.NotNull(adv);
        Assert.Null(adv!.LeftEarbudBattery);
        Assert.NotEqual(100, adv.LeftEarbudBattery);
        Assert.Contains(adv.Diagnostics, d => d.StartsWith(VivoTwsDiagnostics.BatteryOutOfRange, StringComparison.Ordinal));
        Assert.Equal(83, adv.RightEarbudBattery);
    }

    [Fact]
    public void 非TWS格式广播_必须被拒绝()
    {
        var payload = FixtureLoader.Load("non-tws-0x85.hex");

        var ok = VivoTwsAdvertisementParser.TryParse(payload, out var adv, out var status);

        Assert.False(ok);
        Assert.Null(adv);
        Assert.Equal(VivoTwsParseStatus.NotTwsAdvertisement, status);
    }

    [Fact]
    public void 长度不足_必须被拒绝()
    {
        var payload = FixtureLoader.Load("tws-v1-normal.hex").AsSpan(0, 10).ToArray();

        var ok = VivoTwsAdvertisementParser.TryParse(payload, out var adv, out var status);

        Assert.False(ok);
        Assert.Null(adv);
        Assert.Equal(VivoTwsParseStatus.TooShort, status);
    }

    [Fact]
    public void 空载荷_必须被拒绝()
    {
        var ok = VivoTwsAdvertisementParser.TryParse([], out var adv, out var status);

        Assert.False(ok);
        Assert.Null(adv);
        Assert.Equal(VivoTwsParseStatus.EmptyPayload, status);
    }

    [Fact]
    public void 不支持的格式版本_必须被拒绝()
    {
        var payload = FixtureLoader.Load("tws-v1-normal.hex").ToArray();
        payload[1] = 0x02; // V2 格式本插件未实测，不得解析

        var ok = VivoTwsAdvertisementParser.TryParse(payload, out var adv, out var status);

        Assert.False(ok);
        Assert.Null(adv);
        Assert.Equal(VivoTwsParseStatus.UnsupportedVersion, status);
    }

    [Fact]
    public void MAC_按冒号分隔的大写格式输出()
    {
        var payload = FixtureLoader.Load("tws-v1-normal.hex");

        VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _);

        Assert.Equal("02:00:00:00:00:00", adv!.MacAddress);
    }

    [Fact]
    public void 十六进制字符串解析_支持空格与冒号分隔()
    {
        Assert.True(VivoTwsAdvertisementParser.TryParseHex("08 01:02", out var bytes));
        Assert.Equal([0x08, 0x01, 0x02], bytes);

        Assert.False(VivoTwsAdvertisementParser.TryParseHex("0", out _));
        Assert.False(VivoTwsAdvertisementParser.TryParseHex("", out _));
        Assert.False(VivoTwsAdvertisementParser.TryParseHex("ZZ", out _));
    }
}
