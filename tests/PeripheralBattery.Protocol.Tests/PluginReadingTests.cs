using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.Protocol.Tests;

/// <summary>
/// 插件读数模型测试。
/// </summary>
/// <remarks>
/// 锁定「降级为过期读数时必须保留设备标识」这一契约：
/// 宿主按 deviceId 把读数归并到具体设备，标识一旦丢失，
/// 同一台真实设备旁边会多出一台名为「{插件 id}:default」的幽灵设备。
/// </remarks>
public sealed class PluginReadingTests
{
    [Fact]
    public void 标记过期必须保留设备标识与全部字段()
    {
        var reading = new PluginReading
        {
            DeviceId = "bt:1C:11:2F:27:89:7A",
            DeviceName = "vivo TWS 3i",
            Component = "leftEarbud",
            Percentage = 99,
            Charging = false,
            Connected = true,
            Quality = "reported",
            Source = "BLE-Advertisement:0x0837",
        };

        var stale = reading.MarkStale("NO_FRESH_ADVERTISEMENT_IN_WINDOW");

        Assert.Equal("bt:1C:11:2F:27:89:7A", stale.DeviceId);
        Assert.Equal("vivo TWS 3i", stale.DeviceName);
        Assert.Equal("leftEarbud", stale.Component);
        Assert.Equal(99, stale.Percentage);
        Assert.False(stale.Charging);
        Assert.True(stale.Connected);
        Assert.Equal("BLE-Advertisement:0x0837", stale.Source);
        Assert.Equal(nameof(ReadingQuality.Stale), stale.Quality);
        Assert.Equal("NO_FRESH_ADVERTISEMENT_IN_WINDOW", stale.DiagnosticCode);
    }

    [Fact]
    public void 标记过期不得改动原读数()
    {
        var reading = new PluginReading
        {
            DeviceId = "bt:x",
            Component = "leftEarbud",
            Quality = "reported",
        };

        _ = reading.MarkStale("X");

        Assert.Equal("reported", reading.Quality);
        Assert.Null(reading.DiagnosticCode);
    }

    [Fact]
    public void 标记过期不得丢失未知电量的语义()
    {
        var reading = new PluginReading
        {
            DeviceId = "bt:x",
            Component = "leftEarbud",
            Percentage = null,
            Quality = "unavailable",
        };

        var stale = reading.MarkStale("X");

        // null 是「未知」，0 是「真实电量」，二者不得混淆。
        Assert.Null(stale.Percentage);
    }
}
