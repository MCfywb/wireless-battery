using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.Core;

/// <summary>
/// 设备状态存储：按 (DeviceId, Component) 归并读数，负责去重、过期降级与诊断记录。
/// </summary>
/// <remarks>
/// <para>本类是宿主侧数据规则的最终防线，实现 docs/development-plan.md §4.2：</para>
/// <list type="bullet">
/// <item><description><c>Percentage</c> 只能是 <c>null</c> 或 0..100；越界值记诊断并按未知处理，<b>不钳制</b>。</description></item>
/// <item><description><c>0%</c> 是真实电量，<c>null</c> 是未知，二者严格区分。</description></item>
/// <item><description>各部件独立：某帧缺失的部件不会从其他部件继承数值。</description></item>
/// <item><description>超过过期窗口的读数降级为 <c>Stale</c>，保留最后成功时刻，不得当作实时值展示。</description></item>
/// </list>
/// </remarks>
public sealed class DeviceStateStore
{
    /// <summary>越界电量值的诊断码。</summary>
    public const string DiagnosticOutOfRange = "PERCENTAGE_OUT_OF_RANGE";

    /// <summary>读数过期降级的诊断码。</summary>
    public const string DiagnosticExpired = "READING_EXPIRED";

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    private readonly TimeSpan _staleAfter;
    private readonly List<string> _diagnostics = [];

    /// <summary>构造存储。</summary>
    /// <param name="staleAfter">读数过期窗口；超过此时长未更新即降级为 <c>Stale</c>。</param>
    public DeviceStateStore(TimeSpan staleAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);
        _staleAfter = staleAfter;
    }

    /// <summary>过期窗口。</summary>
    public TimeSpan StaleAfter => _staleAfter;

    /// <summary>
    /// 应用一批读数。
    /// </summary>
    /// <param name="readings">待应用的读数。</param>
    /// <param name="now">当前时刻。</param>
    /// <returns>是否有实质变化（数值/质量/状态改变）。观测时间推进不算实质变化，避免 UI 频繁重建。</returns>
    public bool Apply(IEnumerable<BatteryReading> readings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(readings);

        lock (_gate)
        {
            var changed = false;
            foreach (var reading in readings)
            {
                if (ApplyCore(reading, now))
                {
                    changed = true;
                }
            }

            return changed;
        }
    }

    /// <summary>
    /// 对超过过期窗口的读数做降级处理。
    /// </summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>是否有部件被降级。</returns>
    public bool Expire(DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var device in _devices.Values)
            {
                foreach (var component in device.Components.Values)
                {
                    if (component.Quality != ReadingQuality.Reported)
                    {
                        continue;
                    }

                    if (now - component.ObservedAtUtc <= _staleAfter)
                    {
                        continue;
                    }

                    device.Components[component.Component] = component with
                    {
                        Quality = ReadingQuality.Stale,
                        DiagnosticCode = DiagnosticExpired,
                    };
                    changed = true;
                }
            }

            return changed;
        }
    }

    /// <summary>记录设备级错误（如插件返回 DEVICE_NOT_FOUND）。</summary>
    public void RecordDeviceError(string deviceId, string errorCode)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out var device))
            {
                device.LastErrorCode = errorCode;
            }
            else
            {
                _devices[deviceId] = new DeviceState
                {
                    DeviceId = deviceId,
                    DeviceName = deviceId,
                    PluginId = string.Empty,
                    LastErrorCode = errorCode,
                };
            }
        }
    }

    /// <summary>记录插件级错误：回写到该插件名下的所有设备。</summary>
    /// <remarks>
    /// 插件返回纯错误响应（如 <c>DEVICE_NOT_FOUND</c>）时读数为空，
    /// 无从知道要作用到哪台设备。早期实现此时什么都不做，
    /// 导致 UI 一直显示冻结的旧数值却不给出任何原因，
    /// 用户无法区分「程序卡住」与「设备没广播」。
    /// </remarks>
    public void RecordPluginError(string pluginId, string errorCode)
    {
        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                if (!string.Equals(device.PluginId, pluginId, StringComparison.Ordinal))
                {
                    continue;
                }

                device.LastErrorCode = errorCode;
            }
        }
    }

    /// <summary>清除插件名下设备的错误标记（轮询重新成功后调用）。</summary>
    /// <returns>是否清除了任何标记。</returns>
    public bool ClearPluginError(string pluginId)
    {
        lock (_gate)
        {
            var cleared = false;
            foreach (var device in _devices.Values)
            {
                if (!string.Equals(device.PluginId, pluginId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (device.LastErrorCode is null)
                {
                    continue;
                }

                device.LastErrorCode = null;
                cleared = true;
            }

            return cleared;
        }
    }

    /// <summary>记录一条诊断信息（保留最近若干条）。</summary>
    public void AddDiagnostic(string message)
    {
        lock (_gate)
        {
            _diagnostics.Add(message);
            if (_diagnostics.Count > 200)
            {
                _diagnostics.RemoveRange(0, _diagnostics.Count - 200);
            }
        }
    }

    /// <summary>获取当前全部设备快照。</summary>
    public IReadOnlyList<DeviceSnapshot> GetDevices()
    {
        lock (_gate)
        {
            return _devices.Values
                .OrderBy(d => d.DeviceName, StringComparer.CurrentCulture)
                .Select(ToSnapshot)
                .ToList();
        }
    }

    /// <summary>获取单个设备快照。</summary>
    public DeviceSnapshot? GetDevice(string deviceId)
    {
        lock (_gate)
        {
            return _devices.TryGetValue(deviceId, out var device) ? ToSnapshot(device) : null;
        }
    }

    /// <summary>获取最近的诊断信息副本。</summary>
    public IReadOnlyList<string> GetDiagnostics()
    {
        lock (_gate)
        {
            return _diagnostics.ToList();
        }
    }

    private bool ApplyCore(BatteryReading reading, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reading.DeviceId))
        {
            _diagnostics.Add("丢弃缺少 DeviceId 的读数");
            return false;
        }

        if (!_devices.TryGetValue(reading.DeviceId, out var device))
        {
            device = new DeviceState
            {
                DeviceId = reading.DeviceId,
                DeviceName = reading.DeviceName,
                PluginId = reading.PluginId,
            };
            _devices[reading.DeviceId] = device;
        }

        if (!string.IsNullOrWhiteSpace(reading.DeviceName))
        {
            device.DeviceName = reading.DeviceName;
        }

        if (!string.IsNullOrWhiteSpace(reading.PluginId))
        {
            device.PluginId = reading.PluginId;
        }

        var percentage = reading.Percentage;
        string? diagnostic = reading.DiagnosticCode;

        if (percentage is { } value && (value < 0 || value > 100))
        {
            // 越界值是无效数据：记诊断并按未知处理，不得钳制成 0 或 100（§4.2）。
            _diagnostics.Add(
                $"{reading.DeviceId}/{reading.Component} 电量越界：{value}（按未知处理，未钳制）");
            percentage = null;
            diagnostic = DiagnosticOutOfRange;
        }

        var quality = reading.Quality;
        if (percentage is null && quality == ReadingQuality.Reported)
        {
            // 声称已报告却没有数值：按未知处理，避免 UI 显示空白却标为"实时"。
            quality = ReadingQuality.Unavailable;
        }

        // 断开连接的设备不得继续把旧数值当作实时值（§4.2）。
        if (reading.IsConnected == false && quality == ReadingQuality.Reported)
        {
            quality = ReadingQuality.Stale;
        }

        var lastSuccess = reading.Quality == ReadingQuality.Reported
            ? reading.LastSuccessfulReadUtc ?? now
            : reading.LastSuccessfulReadUtc;

        var candidate = new ComponentSnapshot(
            reading.Component,
            percentage,
            reading.IsCharging,
            reading.IsConnected,
            quality,
            reading.ObservedAtUtc,
            lastSuccess,
            reading.Source,
            diagnostic);

        if (device.Components.TryGetValue(reading.Component, out var existing)
            && IsEquivalent(existing, candidate))
        {
            // 仅观测时间推进：不算实质变化。
            device.Components[reading.Component] = candidate;
            return false;
        }

        device.Components[reading.Component] = candidate;
        if (candidate.Quality == ReadingQuality.Reported)
        {
            device.LastSuccessfulReadUtc = candidate.LastSuccessfulReadUtc;
        }

        return true;
    }

    /// <summary>
    /// 判断两个部件读数是否在语义上等价（忽略观测时间）。
    /// </summary>
    private static bool IsEquivalent(ComponentSnapshot a, ComponentSnapshot b)
        => a.Percentage == b.Percentage
           && a.IsCharging == b.IsCharging
           && a.IsConnected == b.IsConnected
           && a.Quality == b.Quality
           && string.Equals(a.DiagnosticCode, b.DiagnosticCode, StringComparison.Ordinal)
           && string.Equals(a.Source, b.Source, StringComparison.Ordinal);

    private static DeviceSnapshot ToSnapshot(DeviceState device) => new(
        device.DeviceId,
        device.DeviceName,
        device.PluginId,
        new Dictionary<BatteryComponent, ComponentSnapshot>(device.Components),
        device.LastSuccessfulReadUtc,
        device.IsConnected,
        device.LastErrorCode);

    private sealed class DeviceState
    {
        public required string DeviceId { get; init; }

        public string DeviceName { get; set; } = string.Empty;

        public string PluginId { get; set; } = string.Empty;

        public DateTimeOffset? LastSuccessfulReadUtc { get; set; }

        public bool? IsConnected { get; set; }

        public string? LastErrorCode { get; set; }

        public Dictionary<BatteryComponent, ComponentSnapshot> Components { get; } = [];
    }
}
