using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.PluginHost;

/// <summary>响应校验结果。</summary>
/// <param name="IsValid">是否通过校验。</param>
/// <param name="Error">失败原因；通过时为 <c>null</c>。</param>
/// <param name="Readings">转换后的读数（已把 component 字符串映射为枚举）。</param>
public sealed record PluginResponseValidation(bool IsValid, string? Error, IReadOnlyList<BatteryReading> Readings)
{
    /// <summary>构造失败结果。</summary>
    public static PluginResponseValidation Fail(string error) => new(false, error, []);
}

/// <summary>
/// Worker 响应校验器。实现 docs/development-plan.md §5.3「所有输出由宿主校验」。
/// </summary>
/// <remarks>
/// 校验项：协议版本、requestId 匹配、部件名合法、电量值范围、读数条数上限、
/// 诊断条数上限、错误消息长度上限、同设备同部件重复。
/// </remarks>
public static class PluginResponseValidator
{
    /// <summary>校验并转换响应。</summary>
    /// <param name="response">待校验的响应。</param>
    /// <param name="expectedRequestId">期望的 requestId。</param>
    /// <param name="pluginId">产出读数的插件 id。</param>
    /// <param name="options">主机配置（上限约束）。</param>
    /// <param name="observedAtUtc">观测时刻。</param>
    public static PluginResponseValidation Validate(
        PluginResponse response,
        string expectedRequestId,
        string pluginId,
        PluginHostOptions options,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);

        if (response.ProtocolVersion != PluginProtocol.ProtocolVersion)
        {
            return PluginResponseValidation.Fail(
                $"响应协议版本不匹配：{response.ProtocolVersion}（宿主支持 {PluginProtocol.ProtocolVersion}）");
        }

        if (!string.Equals(response.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            return PluginResponseValidation.Fail(
                $"响应 requestId 不匹配：期望 {expectedRequestId}，实际 \"{response.RequestId}\"");
        }

        if (response.Diagnostics.Count > options.MaxDiagnosticEntries)
        {
            return PluginResponseValidation.Fail(
                $"诊断条目过多：{response.Diagnostics.Count} > {options.MaxDiagnosticEntries}");
        }

        if (response.Error is { } error)
        {
            if (error.Message.Length > options.MaxErrorMessageLength)
            {
                return PluginResponseValidation.Fail(
                    $"错误消息超长：{error.Message.Length} > {options.MaxErrorMessageLength}");
            }

            if (string.IsNullOrWhiteSpace(error.Code))
            {
                return PluginResponseValidation.Fail("错误响应缺少错误码");
            }
        }

        if (response.Readings.Count > options.MaxReadings)
        {
            return PluginResponseValidation.Fail(
                $"读数条目过多：{response.Readings.Count} > {options.MaxReadings}");
        }

        var readings = new List<BatteryReading>(response.Readings.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 先解析本次响应里的设备标识（§4.2 按稳定设备 ID 去重）：
        // - 若响应中存在且仅存在一个已标识设备，未标识的读数继承该 id；
        //   否则同一次响应里的同一台设备会被拆成「真实设备 + {插件 id}:default」两台幽灵设备。
        // - 若一个标识都没有（兼容未返回 deviceId 的旧 Worker），退化为插件内唯一设备标识。
        var identifiedIds = new HashSet<string>(StringComparer.Ordinal);
        var identifiedNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var reading in response.Readings)
        {
            if (string.IsNullOrWhiteSpace(reading.DeviceId))
            {
                continue;
            }

            identifiedIds.Add(reading.DeviceId);
            if (!string.IsNullOrWhiteSpace(reading.DeviceName))
            {
                identifiedNames.TryAdd(reading.DeviceId, reading.DeviceName);
            }
        }

        string? inheritedDeviceId = identifiedIds.Count == 1 ? identifiedIds.First() : null;

        foreach (var reading in response.Readings)
        {
            if (!TryParseComponent(reading.Component, out var component))
            {
                return PluginResponseValidation.Fail($"未知部件名：\"{reading.Component}\"");
            }

            if (reading.Percentage is { } percentage && (percentage < 0 || percentage > 100))
            {
                // 越界值是无效数据：拒绝整条响应，不得钳制（§4.2）。
                return PluginResponseValidation.Fail($"电量越界：{reading.Component}={percentage}");
            }

            if (!TryParseQuality(reading.Quality, out var quality))
            {
                return PluginResponseValidation.Fail($"未知读数质量：\"{reading.Quality}\"");
            }

            var deviceId = reading.DeviceId;
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = inheritedDeviceId ?? $"{pluginId}:default";
            }

            var deviceName = !string.IsNullOrWhiteSpace(reading.DeviceName)
                ? reading.DeviceName
                : identifiedNames.GetValueOrDefault(deviceId, deviceId);

            // 同设备同部件不得重复，否则归并时无法确定取哪一个。
            var key = $"{deviceId}|{component}";
            if (!seen.Add(key))
            {
                return PluginResponseValidation.Fail($"重复读数：{key}");
            }

            readings.Add(new BatteryReading(
                deviceId,
                deviceName,
                pluginId,
                component,
                reading.Percentage,
                reading.Charging,
                reading.Connected,
                quality,
                observedAtUtc,
                quality == ReadingQuality.Reported ? observedAtUtc : null,
                reading.Source ?? string.Empty,
                reading.DiagnosticCode));
        }

        return new PluginResponseValidation(true, null, readings);
    }

    /// <summary>把部件名字符串映射为枚举。</summary>
    public static bool TryParseComponent(string? name, out BatteryComponent component)
    {
        component = BatteryComponent.Unknown;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        switch (name.Trim().ToLowerInvariant())
        {
            case "main":
                component = BatteryComponent.Main;
                return true;
            case "leftearbud":
            case "left":
                component = BatteryComponent.LeftEarbud;
                return true;
            case "rightearbud":
            case "right":
                component = BatteryComponent.RightEarbud;
                return true;
            case "chargingcase":
            case "case":
                component = BatteryComponent.ChargingCase;
                return true;
            case "receiver":
                component = BatteryComponent.Receiver;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把质量字符串映射为枚举。</summary>
    public static bool TryParseQuality(string? name, out ReadingQuality quality)
    {
        quality = ReadingQuality.Unavailable;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        switch (name.Trim().ToLowerInvariant())
        {
            case "reported":
                quality = ReadingQuality.Reported;
                return true;
            case "estimated":
                quality = ReadingQuality.Estimated;
                return true;
            case "stale":
                quality = ReadingQuality.Stale;
                return true;
            case "unavailable":
                quality = ReadingQuality.Unavailable;
                return true;
            default:
                return false;
        }
    }
}
