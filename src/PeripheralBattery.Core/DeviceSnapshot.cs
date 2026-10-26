using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.Core;

/// <summary>
/// 单个部件在设备快照中的呈现。
/// </summary>
/// <param name="Component">部件。</param>
/// <param name="Percentage">电量百分比；<c>null</c> 表示未知/未提供。<c>0</c> 是真实电量，与 <c>null</c> 严格区分。</param>
/// <param name="IsCharging">是否充电中；协议未报告时为 <c>null</c>。</param>
/// <param name="IsConnected">是否已连接；协议未报告时为 <c>null</c>。</param>
/// <param name="Quality">读数质量。</param>
/// <param name="ObservedAtUtc">本次观测时刻。</param>
/// <param name="LastSuccessfulReadUtc">最后一次成功读到该部件数值的时刻。</param>
/// <param name="Source">数据来源标识，便于诊断。</param>
/// <param name="DiagnosticCode">诊断码；无异常时为 <c>null</c>。</param>
public sealed record ComponentSnapshot(
    BatteryComponent Component,
    int? Percentage,
    bool? IsCharging,
    bool? IsConnected,
    ReadingQuality Quality,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastSuccessfulReadUtc,
    string Source,
    string? DiagnosticCode)
{
    /// <summary>是否持有可显示的数值（未知态为 <c>false</c>）。</summary>
    public bool HasValue => Percentage is not null;

    /// <summary>是否已过期（曾经有效但现在不是实时值）。</summary>
    public bool IsStale => Quality == ReadingQuality.Stale;
}

/// <summary>
/// 设备快照。同一设备可由不同插件提供读数，这里按 (DeviceId, Component) 归并。
/// </summary>
/// <param name="DeviceId">稳定的本地设备标识。</param>
/// <param name="DeviceName">设备显示名。</param>
/// <param name="PluginId">产出该读数的插件 id。</param>
/// <param name="Components">各部件读数。</param>
/// <param name="LastSuccessfulReadUtc">该设备最后一次成功读取的时刻。</param>
/// <param name="IsConnected">连接状态；协议未报告时为 <c>null</c>。</param>
/// <param name="LastErrorCode">最近一次读取失败的错误码；无失败时为 <c>null</c>。</param>
public sealed record DeviceSnapshot(
    string DeviceId,
    string DeviceName,
    string PluginId,
    IReadOnlyDictionary<BatteryComponent, ComponentSnapshot> Components,
    DateTimeOffset? LastSuccessfulReadUtc,
    bool? IsConnected,
    string? LastErrorCode = null)
{
    /// <summary>取指定部件读数；不存在时返回 <c>null</c>。</summary>
    public ComponentSnapshot? Get(BatteryComponent component)
        => Components.TryGetValue(component, out var value) ? value : null;

    /// <summary>
    /// 最低的有效电量百分比；无任何有效读数时返回 <c>null</c>。
    /// </summary>
    /// <remarks>只统计 <see cref="ReadingQuality.Reported"/> 的部件，过期值不参与最低值计算，
    /// 以免托盘把陈旧数字当作当前电量提示。</remarks>
    public int? LowestReportedPercentage
    {
        get
        {
            int? lowest = null;
            foreach (var c in Components.Values)
            {
                if (c.Quality != ReadingQuality.Reported || c.Percentage is not { } p)
                {
                    continue;
                }

                if (lowest is null || p < lowest.Value)
                {
                    lowest = p;
                }
            }

            return lowest;
        }
    }
}
