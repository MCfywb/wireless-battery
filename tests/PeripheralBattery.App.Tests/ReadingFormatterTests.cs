using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.App.Tests;

/// <summary>
/// 读数/状态码到 UI 文案的映射测试。
/// </summary>
public sealed class ReadingFormatterTests
{
    [Fact]
    public void 已连上但无新数据不得显示成设备离线()
    {
        // 主动通道已连入却没收到上报，必须与「设备未找到」明确区分，
        // 否则用户会误以为耳机离线。
        var noFresh = ReadingFormatter.DescribeErrorCode(PluginErrorCodes.NoFreshReport);
        var notFound = ReadingFormatter.DescribeErrorCode(PluginErrorCodes.DeviceNotFound);

        Assert.Contains("未收到新读数", noFresh, StringComparison.Ordinal);
        Assert.Contains("未收到设备广播", notFound, StringComparison.Ordinal);
        Assert.NotEqual(noFresh, notFound);
    }

    [Theory]
    [InlineData(PluginErrorCodes.NoFreshReport)]
    [InlineData(PluginErrorCodes.DeviceNotFound)]
    [InlineData(PluginErrorCodes.DeviceNotConnected)]
    [InlineData(PluginErrorCodes.UnsupportedRequest)]
    [InlineData(PluginErrorCodes.ProtocolMismatch)]
    [InlineData(PluginErrorCodes.InternalError)]
    public void 已知错误码必须有中文说明(string code)
    {
        var text = ReadingFormatter.DescribeErrorCode(code);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(code, text);
    }

    [Fact]
    public void 未知错误码应原样返回以便排查()
    {
        Assert.Equal("SOMETHING_NEW", ReadingFormatter.DescribeErrorCode("SOMETHING_NEW"));
        Assert.Equal("未知错误", ReadingFormatter.DescribeErrorCode(null));
    }
}
