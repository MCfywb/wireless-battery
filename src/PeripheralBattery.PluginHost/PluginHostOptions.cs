namespace PeripheralBattery.PluginHost;

/// <summary>插件运行时健康状态。</summary>
public enum PluginHealthState
{
    /// <summary>尚未启动。</summary>
    NotStarted,

    /// <summary>正在启动 Worker 进程。</summary>
    Starting,

    /// <summary>正常运行，最近一次读取成功。</summary>
    Healthy,

    /// <summary>运行中但最近一次读取失败（尚未达到熔断阈值）。</summary>
    Degraded,

    /// <summary>连续失败达到阈值，已停止轮询，需用户重启。</summary>
    CircuitOpen,

    /// <summary>已被显式停止。</summary>
    Stopped,
}

/// <summary>插件主机配置。</summary>
public sealed class PluginHostOptions
{
    /// <summary>
    /// 轮询请求超时。
    /// </summary>
    /// <remarks>
    /// 方案建议普通 poll 初始 3 秒，但本仓库实测 vivo TWS 3i 的广播扫描窗口为 12 秒
    /// （Fast Pair 广播可降到约 10 秒一条），超时必须大于插件内部扫描窗口，否则必然超时。
    /// 该值须由实测调优（§5.3）。
    /// </remarks>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>发现请求超时（需覆盖设备枚举耗时）。</summary>
    public TimeSpan DiscoverTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>诊断请求超时。</summary>
    public TimeSpan DiagnosticsTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>关闭请求超时。</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 连续失败多少次后进入熔断状态。
    /// </summary>
    /// <remarks>单个插件崩溃不允许使宿主退出；连续失败达阈值后熔断并通知 UI，可由用户重启（§5.3）。</remarks>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Worker 启动后等待其就绪的时间。</summary>
    public TimeSpan StartupGrace { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>崩溃后自动重启的最短间隔（指数退避上限）。</summary>
    public TimeSpan RestartBackoffMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>保留的 Worker stderr 行数上限。</summary>
    public int MaxStderrLines { get; set; } = 200;

    /// <summary>允许的最大响应字节数（超出视为协议违规）。</summary>
    public int MaxResponseBytes { get; set; } = 256 * 1024;

    /// <summary>单次响应允许的最大诊断条目数。</summary>
    public int MaxDiagnosticEntries { get; set; } = 100;

    /// <summary>单次响应允许的最大读数条目数。</summary>
    public int MaxReadings { get; set; } = 64;

    /// <summary>允许的最大错误消息长度（超出视为协议违规）。</summary>
    public int MaxErrorMessageLength { get; set; } = 1024;
}
