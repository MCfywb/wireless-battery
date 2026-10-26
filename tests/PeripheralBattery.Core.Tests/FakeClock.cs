using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.Core.Tests;

/// <summary>可注入固定时间的时钟。</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}
