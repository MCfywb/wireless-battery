namespace PeripheralBattery.Core;

/// <summary>
/// 时钟抽象。生产环境用 <see cref="SystemClock"/>，测试注入固定时间以验证过期规则。
/// </summary>
public interface IClock
{
    /// <summary>当前 UTC 时刻。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>系统时钟。</summary>
public sealed class SystemClock : IClock
{
    /// <summary>共享实例。</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
