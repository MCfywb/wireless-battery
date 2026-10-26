using PeripheralBattery.Core;

namespace PeripheralBattery.App;

/// <summary>
/// 读数到 UI 文案的映射。
/// </summary>
/// <remarks>
/// 严格遵守 docs/development-plan.md §4.2：未知显示「--」而不是 0；
/// 过期值必须带过期标记与最后成功时间，不得显示成实时值。
/// </remarks>
public static class ReadingFormatter
{
    /// <summary>格式化为「--」表示未知。</summary>
    public const string UnknownText = "--";

    /// <summary>部件中文名。</summary>
    public static string ComponentName(PluginSdk.BatteryComponent component) => component switch
    {
        PluginSdk.BatteryComponent.Main => "主设备",
        PluginSdk.BatteryComponent.LeftEarbud => "左耳",
        PluginSdk.BatteryComponent.RightEarbud => "右耳",
        PluginSdk.BatteryComponent.ChargingCase => "充电盒",
        PluginSdk.BatteryComponent.Receiver => "接收器",
        _ => "未知",
    };

    /// <summary>
    /// 格式化单个部件读数。
    /// </summary>
    public static string Format(ComponentSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Percentage is not { } percentage)
        {
            // 未知就是未知，绝不显示 0%（§4.2）。
            return snapshot.DiagnosticCode is { } code && code.Length > 0
                ? $"{UnknownText}（{code}）"
                : UnknownText;
        }

        var text = $"{percentage}%";

        if (snapshot.Quality == PluginSdk.ReadingQuality.Stale)
        {
            // 曾经有效但已过期：必须显示过期标记与最后成功时间。
            var age = snapshot.LastSuccessfulReadUtc is { } last ? FormatAge(now - last) : null;
            text = age is null ? $"{text}（已过期）" : $"{text}（上次 {age}）";
        }
        else if (snapshot.Quality == PluginSdk.ReadingQuality.Estimated)
        {
            text = $"{text}（估算）";
        }
        else if (snapshot.Quality == PluginSdk.ReadingQuality.Unavailable)
        {
            text = $"{text}（不可用）";
        }

        if (snapshot.IsCharging == true)
        {
            text += " 充电中";
        }

        return text;
    }

    /// <summary>
    /// 把插件错误码翻译成用户能看懂的说明。
    /// </summary>
    /// <remarks>
    /// 必须准确区分「设备不在线」与「已连上但没新数据」——
    /// 后者若显示成「找不到设备」会让用户误以为耳机离线。
    /// </remarks>
    /// <param name="errorCode">错误码。</param>
    /// <returns>中文说明；未知错误码原样返回。</returns>
    public static string DescribeErrorCode(string? errorCode) => errorCode switch
    {
        PluginSdk.PluginErrorCodes.NoFreshReport =>
            "已连上设备，但本次未收到新读数（耳机状态未变化时不推送）",
        PluginSdk.PluginErrorCodes.DeviceNotFound =>
            "扫描窗口内未收到设备广播",
        PluginSdk.PluginErrorCodes.DeviceNotConnected =>
            "设备未连接，无法建立读取通道",
        PluginSdk.PluginErrorCodes.UnsupportedRequest =>
            "该插件不支持此操作",
        PluginSdk.PluginErrorCodes.ProtocolMismatch =>
            "插件协议版本不匹配",
        PluginSdk.PluginErrorCodes.InternalError =>
            "插件内部错误",
        _ => errorCode ?? "未知错误",
    };
    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(1, (int)age.TotalSeconds)} 秒前";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes} 分钟前";
        }

        if (age.TotalHours < 24)
        {
            return $"{(int)age.TotalHours} 小时前";
        }

        return $"{(int)age.TotalDays} 天前";
    }
}
