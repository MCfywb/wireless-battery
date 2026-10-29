namespace PeripheralBattery.PluginSdk;

/// <summary>
/// 统一电量读数。字段定义取自 docs/development-plan.md §4.1。
/// </summary>
/// <remarks>
/// 数据规则（§4.2）：
/// <list type="bullet">
/// <item><description><c>Percentage</c> 只能是 <c>null</c> 或 0..100；越界值不得钳制，应记诊断。</description></item>
/// <item><description><c>0%</c> 是真实电量，<c>null</c> 是未知，两者严格区分。</description></item>
/// <item><description>三个部件必须分别返回；某帧缺失的部件不得继承其他部件的值。</description></item>
/// </list>
/// </remarks>
/// <param name="DeviceId">稳定的本地设备标识（不得写入导出诊断包）。</param>
/// <param name="DeviceName">设备显示名。</param>
/// <param name="PluginId">产出该读数的插件 id。</param>
/// <param name="Component">部件。</param>
/// <param name="Percentage">电量百分比，<c>null</c> 表示未知/未提供/无效。</param>
/// <param name="IsCharging">是否充电中，协议未报告时为 <c>null</c>。</param>
/// <param name="IsConnected">是否已连接，协议未报告时为 <c>null</c>。</param>
/// <param name="Quality">读数质量。</param>
/// <param name="ObservedAtUtc">本次观测时刻。</param>
/// <param name="LastSuccessfulReadUtc">最后一次成功读数时刻。</param>
/// <param name="Source">数据来源，如 <c>BLE-Advertisement:0x0837</c>。</param>
/// <param name="DiagnosticCode">诊断码，无异常时为 <c>null</c>。</param>
/// <param name="IsPresent">
/// 插件给出的<b>在线判定</b>：<c>true</c> = 设备在场、<c>false</c> = 已判定离线（宿主应移除该设备条目）、
/// <c>null</c> = 插件未作判定（保持原有行为，不移除）。
/// </param>
/// <remarks>
/// <b>为什么与 <see cref="IsConnected"/> 分开</b>：两者语义不同，不能互相替代。
/// <see cref="IsConnected"/> 指「与本机是否保持经典蓝牙连接」，在线判定则是
/// 「这台设备现在还在不在」——后者由<b>近期有广播 OR 经典蓝牙已连接</b>合成
/// （两条路径互补盲区：连着本机放歌时广播被压制，靠连接态；未连本机时靠广播）。
/// 若把「未与本机连接」误当成离线，会让靠广播正常取数的设备被错判。
/// </remarks>
public sealed record BatteryReading(
    string DeviceId,
    string DeviceName,
    string PluginId,
    BatteryComponent Component,
    int? Percentage,
    bool? IsCharging,
    bool? IsConnected,
    ReadingQuality Quality,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastSuccessfulReadUtc,
    string Source,
    string? DiagnosticCode = null,
    bool? IsPresent = null);
