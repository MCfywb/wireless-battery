using VivoTws3i.Protocol;
using Xunit;

namespace PeripheralBattery.Protocol.Tests;

/// <summary>
/// 在线判定去抖器的测试。
/// </summary>
/// <remarks>
/// 针对 2026-10-30 两次真机实测发现的<b>断电瞬时回连</b>现象：
/// <c>ConnectionStatus</c> 在耳机断电过程中会短暫翻回 Connected（一次 3 秒后、
/// 另一次 17 秒后，两次时刻还不一样）。若不去抖，设备其实已走时会误判在线。
/// </remarks>
public class PresenceDebouncerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 初始未形成任何判定()
    {
        var debouncer = new PresenceDebouncer(TimeSpan.FromSeconds(3));

        Assert.Null(debouncer.Stable);
    }

    [Fact]
    public void 候选值需稳定满窗口才升为稳定值()
    {
        var debouncer = new PresenceDebouncer(TimeSpan.FromSeconds(3));

        // 第 1 次采样只是建立候选，不应立刻判定。
        Assert.Null(debouncer.Update(true, T0));
        Assert.Null(debouncer.Stable);

        // 未满足窗口仍不判定。
        Assert.Null(debouncer.Update(true, T0.AddSeconds(2)));
        Assert.Null(debouncer.Stable);

        // 满足窗口后才判定。
        Assert.True(debouncer.Update(true, T0.AddSeconds(3)));
        Assert.True(debouncer.Stable);
    }

    /// <summary>核心用例：断电过程中的瞬时回连不得翻转判定。</summary>
    [Fact]
    public void 瞬时翻转不得改变稳定值()
    {
        var debouncer = new PresenceDebouncer(TimeSpan.FromSeconds(3));

        // 先稳定判定为「在线」。
        _ = debouncer.Update(true, T0);
        _ = debouncer.Update(true, T0.AddSeconds(3));
        Assert.True(debouncer.Stable);

        // 断电：翻成 false，但只持续 2 秒（短于去抖窗口）就又翻回 true。
        // 这正是实测中"6.04s 断、9.07s 又连"的场景。
        Assert.True(debouncer.Update(false, T0.AddSeconds(10)));
        Assert.True(debouncer.Stable);

        Assert.True(debouncer.Update(true, T0.AddSeconds(12)));
        Assert.True(debouncer.Stable);
    }

    [Fact]
    public void 持续断开满窗口后才判定为离线()
    {
        var debouncer = new PresenceDebouncer(TimeSpan.FromSeconds(3));

        _ = debouncer.Update(true, T0);
        _ = debouncer.Update(true, T0.AddSeconds(3));
        Assert.True(debouncer.Stable);

        _ = debouncer.Update(false, T0.AddSeconds(10));
        Assert.True(debouncer.Stable);

        Assert.False(debouncer.Update(false, T0.AddSeconds(13)));
        Assert.False(debouncer.Stable);
    }

    /// <summary>采样失败（null）应保留上一次结论，不能翻转状态。</summary>
    [Fact]
    public void 采样失败时保留上一次结论()
    {
        var debouncer = new PresenceDebouncer(TimeSpan.FromSeconds(3));

        _ = debouncer.Update(true, T0);
        _ = debouncer.Update(true, T0.AddSeconds(3));
        Assert.True(debouncer.Stable);

        Assert.True(debouncer.Update(null, T0.AddSeconds(10)));
        Assert.True(debouncer.Stable);
    }

    [Fact]
    public void 去抖窗口必须为正()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceDebouncer(TimeSpan.Zero));
    }
}
