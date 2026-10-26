namespace PeripheralBattery.PluginSdk;

/// <summary>
/// 电量部件。取自 docs/development-plan.md §4.1。
/// </summary>
public enum BatteryComponent
{
    Main,
    LeftEarbud,
    RightEarbud,
    ChargingCase,
    Receiver,
    Unknown,
}

/// <summary>
/// 读数质量等级。取自 docs/development-plan.md §4.1。
/// </summary>
public enum ReadingQuality
{
    /// <summary>设备直接报告。</summary>
    Reported,

    /// <summary>估算值；UI 必须标记，不得当作精确实测值。</summary>
    Estimated,

    /// <summary>曾经有效但现已过期，不得显示成在线实时值。</summary>
    Stale,

    /// <summary>当前不可获取。</summary>
    Unavailable,
}
