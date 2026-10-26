using VivoTws3i.Protocol;
using Xunit;

namespace PeripheralBattery.Protocol.Tests;

/// <summary>
/// 设备标识归一化测试：保证主动 RFCOMM 与被动广播产出同一个 deviceId。
/// </summary>
/// <remarks>
/// 这正是「出现两个耳机」缺陷的回归测试：经典蓝牙 <c>CanonicalName</c> 形如
/// <c>(1C:11:2F:27:89:7A)</c>（带括号），而广播解析出的 MAC 不带括号，
/// 若不归一，宿主会把同一台耳机当成两台。
/// </remarks>
public sealed class VivoTwsDeviceIdTests
{
    [Theory]
    // 经典蓝牙 CanonicalName 带括号 —— 实测形态
    [InlineData("(1C:11:2F:27:89:7A)", "bt:1C:11:2F:27:89:7A")]
    // 广播 MAC 不带括号
    [InlineData("1C:11:2F:27:89:7A", "bt:1C:11:2F:27:89:7A")]
    // 小写必须转大写
    [InlineData("(1c:11:2f:27:89:7a)", "bt:1C:11:2F:27:89:7A")]
    // 连字符 / 空格分隔也应归一
    [InlineData(" 1C-11-2F-27-89-7A ", "bt:1C:11:2F:27:89:7A")]
    public void 主动通道与被动广播应产出同一设备标识(string raw, string expected)
    {
        Assert.Equal(expected, VivoTwsDeviceId.FromAddress(raw));
    }

    [Theory]
    [InlineData("")]        // 空串
    [InlineData(null)]      // null
    [InlineData("(1C:11)")] // 十六进制字节数不足 6 字节
    public void 无法识别的地址应回落为unknown(string? raw)
    {
        Assert.Equal("unknown", VivoTwsDeviceId.NormalizeAddress(raw));
    }

    [Fact]
    public void 同一台耳机的两种地址形态必须相等()
    {
        var fromRfcomm = VivoTwsDeviceId.FromAddress("(1C:11:2F:27:89:7A)");
        var fromAdvert = VivoTwsDeviceId.FromAddress("1C:11:2F:27:89:7A");

        Assert.Equal(fromRfcomm, fromAdvert);
    }
}
