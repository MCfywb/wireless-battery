using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.PluginHost.Tests;

/// <summary>
/// 响应校验测试：实现 docs/development-plan.md §5.3「所有输出由宿主校验」。
/// </summary>
public sealed class PluginResponseValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static PluginResponse Response(params PluginReading[] readings)
        => new()
        {
            ProtocolVersion = PluginProtocol.ProtocolVersion,
            RequestId = "r-1",
            Ok = true,
            Readings = [.. readings],
        };

    private static PluginResponseValidation Validate(PluginResponse response, PluginHostOptions? options = null)
        => PluginResponseValidator.Validate(response, "r-1", "test-plugin", options ?? new PluginHostOptions(), Now);

    [Fact]
    public void 合法响应应通过校验()
    {
        var response = Response(new PluginReading
        {
            DeviceId = "bt:x",
            DeviceName = "设备",
            Component = "leftEarbud",
            Percentage = 80,
            Quality = "reported",
            Source = "BLE",
        });

        var result = Validate(response);

        Assert.True(result.IsValid);
        Assert.Single(result.Readings);
        Assert.Equal(BatteryComponent.LeftEarbud, result.Readings[0].Component);
        Assert.Equal(80, result.Readings[0].Percentage);
        Assert.Equal("bt:x", result.Readings[0].DeviceId);
    }

    [Fact]
    public void requestId不匹配必须判为错误()
    {
        var response = Response();
        response.RequestId = "r-other";

        var result = Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains("requestId", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 协议版本不匹配必须判为错误()
    {
        var response = Response();
        response.ProtocolVersion = PluginProtocol.ProtocolVersion + 1;

        var result = Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains("协议版本", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void 越界电量必须整条拒绝不得钳制(int outOfRange)
    {
        var response = Response(new PluginReading
        {
            Component = "leftEarbud",
            Percentage = outOfRange,
            Quality = "reported",
        });

        var result = Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains("越界", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 未知部件名必须判为错误()
    {
        var response = Response(new PluginReading
        {
            Component = "spaceStation",
            Percentage = 50,
            Quality = "reported",
        });

        var result = Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains("未知部件名", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 同设备同部件重复必须判为错误()
    {
        var response = Response(
            new PluginReading { DeviceId = "bt:x", Component = "leftEarbud", Percentage = 50, Quality = "reported" },
            new PluginReading { DeviceId = "bt:x", Component = "leftEarbud", Percentage = 60, Quality = "reported" });

        var result = Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains("重复读数", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 超长错误消息必须判为错误()
    {
        var response = Response();
        response.Ok = false;
        response.Error = new PluginError { Code = "X", Message = new string('a', 5000) };

        var result = Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains("错误消息超长", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 读数条目超限必须判为错误()
    {
        var options = new PluginHostOptions { MaxReadings = 2 };
        var response = Response(
            new PluginReading { Component = "leftEarbud", Percentage = 1, Quality = "reported" },
            new PluginReading { Component = "rightEarbud", Percentage = 2, Quality = "reported" },
            new PluginReading { Component = "chargingCase", Percentage = 3, Quality = "reported" });

        var result = Validate(response, options);

        Assert.False(result.IsValid);
        Assert.Contains("读数条目过多", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺少deviceId时回退为插件内唯一标识()
    {
        var response = Response(new PluginReading
        {
            Component = "leftEarbud",
            Percentage = 50,
            Quality = "reported",
        });

        var result = Validate(response);

        Assert.True(result.IsValid);
        Assert.Equal("test-plugin:default", result.Readings[0].DeviceId);
    }

    [Theory]
    [InlineData("leftEarbud", BatteryComponent.LeftEarbud)]
    [InlineData("rightEarbud", BatteryComponent.RightEarbud)]
    [InlineData("chargingCase", BatteryComponent.ChargingCase)]
    [InlineData("main", BatteryComponent.Main)]
    [InlineData("receiver", BatteryComponent.Receiver)]
    public void 部件名映射正确(string name, BatteryComponent expected)
    {
        Assert.True(PluginResponseValidator.TryParseComponent(name, out var component));
        Assert.Equal(expected, component);
    }

    [Theory]
    [InlineData("reported", ReadingQuality.Reported)]
    [InlineData("stale", ReadingQuality.Stale)]
    [InlineData("estimated", ReadingQuality.Estimated)]
    [InlineData("unavailable", ReadingQuality.Unavailable)]
    public void 质量名映射正确(string name, ReadingQuality expected)
    {
        Assert.True(PluginResponseValidator.TryParseQuality(name, out var quality));
        Assert.Equal(expected, quality);
    }

    [Fact]
    public void 未知质量名必须判为错误()
    {
        var response = Response(new PluginReading
        {
            Component = "leftEarbud",
            Percentage = 50,
            Quality = "probablyFine",
        });

        var result = Validate(response);

        Assert.False(result.IsValid);
    }
}
