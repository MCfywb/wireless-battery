using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.Core.Tests;

/// <summary>
/// 设备状态存储测试：覆盖 docs/development-plan.md §4.2 的数据规则。
/// </summary>
public sealed class DeviceStateStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static BatteryReading Reading(
        BatteryComponent component,
        int? percentage,
        ReadingQuality quality = ReadingQuality.Reported,
        bool? connected = true,
        bool? charging = null,
        string deviceId = "bt:test",
        string deviceName = "测试设备",
        string? diagnostic = null)
        => new(
            deviceId,
            deviceName,
            "test-plugin",
            component,
            percentage,
            charging,
            connected,
            quality,
            Now,
            quality == ReadingQuality.Reported ? Now : null,
            "TEST-SOURCE",
            diagnostic);

    [Fact]
    public void Null百分比与零百分比必须区分()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, 0)], Now);
        store.Apply([Reading(BatteryComponent.RightEarbud, null)], Now);

        var device = store.GetDevice("bt:test");
        Assert.NotNull(device);

        var left = device!.Get(BatteryComponent.LeftEarbud);
        var right = device.Get(BatteryComponent.RightEarbud);

        // 0% 是真实电量。
        Assert.Equal(0, left!.Percentage);
        Assert.Equal(ReadingQuality.Reported, left.Quality);

        // null 是未知，不得显示成 0%。
        Assert.Null(right!.Percentage);
        Assert.NotEqual(0, right.Percentage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(255)]
    public void 越界电量必须记诊断且不得钳制(int outOfRange)
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, outOfRange)], Now);

        var device = store.GetDevice("bt:test");
        var component = device!.Get(BatteryComponent.LeftEarbud);

        // 不得钳制成 100 或 0，必须按未知处理。
        Assert.Null(component!.Percentage);
        Assert.Equal(DeviceStateStore.DiagnosticOutOfRange, component.DiagnosticCode);
        Assert.Contains(store.GetDiagnostics(), d => d.Contains("越界", StringComparison.Ordinal));
    }

    [Fact]
    public void 边界值0与100均为合法读数()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply(
        [
            Reading(BatteryComponent.LeftEarbud, 0),
            Reading(BatteryComponent.RightEarbud, 100),
        ],
        Now);

        var device = store.GetDevice("bt:test");
        Assert.Equal(0, device!.Get(BatteryComponent.LeftEarbud)!.Percentage);
        Assert.Equal(100, device.Get(BatteryComponent.RightEarbud)!.Percentage);
    }

    [Fact]
    public void 各部件必须独立不得互相继承()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        // 第一帧只有左耳。
        store.Apply([Reading(BatteryComponent.LeftEarbud, 80)], Now);
        // 第二帧只有右耳，左耳不应被右耳的值覆盖。
        store.Apply([Reading(BatteryComponent.RightEarbud, 30)], Now);

        var device = store.GetDevice("bt:test");
        Assert.Equal(80, device!.Get(BatteryComponent.LeftEarbud)!.Percentage);
        Assert.Equal(30, device.Get(BatteryComponent.RightEarbud)!.Percentage);
    }

    [Fact]
    public void 数值未变时不算实质变化()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        Assert.True(store.Apply([Reading(BatteryComponent.LeftEarbud, 80)], Now));
        // 同样的值再报一次：不算变化。
        Assert.False(store.Apply([Reading(BatteryComponent.LeftEarbud, 80)], Now));
    }

    [Fact]
    public void 数值变化时算实质变化()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, 80)], Now);
        Assert.True(store.Apply([Reading(BatteryComponent.LeftEarbud, 79)], Now));
    }

    [Fact]
    public void 超过过期窗口后降级为Stale并保留数值()
    {
        var clock = new FakeClock();
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, 80)], clock.UtcNow);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(store.Expire(clock.UtcNow));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(store.Expire(clock.UtcNow));

        var component = store.GetDevice("bt:test")!.Get(BatteryComponent.LeftEarbud)!;

        // 曾经有效的数值保留，但质量必须降级，不得当作实时值。
        Assert.Equal(80, component.Percentage);
        Assert.Equal(ReadingQuality.Stale, component.Quality);
        Assert.Equal(DeviceStateStore.DiagnosticExpired, component.DiagnosticCode);
        Assert.NotNull(component.LastSuccessfulReadUtc);
    }

    [Fact]
    public void 断开连接后不得继续标记为实时值()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, 80, connected: false)], Now);

        var component = store.GetDevice("bt:test")!.Get(BatteryComponent.LeftEarbud)!;
        Assert.NotEqual(ReadingQuality.Reported, component.Quality);
    }

    [Fact]
    public void 声称已报告却无数值时按未知处理()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, null)], Now);

        var component = store.GetDevice("bt:test")!.Get(BatteryComponent.LeftEarbud)!;
        Assert.Null(component.Percentage);
        Assert.Equal(ReadingQuality.Unavailable, component.Quality);
    }

    [Fact]
    public void 最低电量只统计已报告的部件()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply(
        [
            Reading(BatteryComponent.LeftEarbud, 80),
            Reading(BatteryComponent.RightEarbud, 30),
            Reading(BatteryComponent.ChargingCase, 95),
        ],
        Now);

        var device = store.GetDevice("bt:test")!;
        Assert.Equal(30, device.LowestReportedPercentage);

        // 过期降级后不参与最低值计算。
        store.Expire(Now + TimeSpan.FromHours(1));
        Assert.Null(store.GetDevice("bt:test")!.LowestReportedPercentage);
    }

    [Fact]
    public void 设备错误码可记录并随快照返回()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, 80)], Now);
        store.RecordDeviceError("bt:test", "DEVICE_NOT_FOUND");

        Assert.Equal("DEVICE_NOT_FOUND", store.GetDevice("bt:test")!.LastErrorCode);
    }

    [Fact]
    public void 不同设备的同名部件不得互相覆盖()
    {
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));

        store.Apply([Reading(BatteryComponent.LeftEarbud, 80, deviceId: "bt:a", deviceName: "A")], Now);
        store.Apply([Reading(BatteryComponent.LeftEarbud, 20, deviceId: "bt:b", deviceName: "B")], Now);

        Assert.Equal(80, store.GetDevice("bt:a")!.Get(BatteryComponent.LeftEarbud)!.Percentage);
        Assert.Equal(20, store.GetDevice("bt:b")!.Get(BatteryComponent.LeftEarbud)!.Percentage);
    }
}
