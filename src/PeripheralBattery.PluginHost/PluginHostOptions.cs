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
    /// <para>本值是<b>防止 Worker 挂死拖垮宿主</b>的唯一屏障：WinRT 蓝牙调用有实测
    /// 挂起 10 秒以上的先例（见 <c>GaiaActiveReader.WithTimeout</c>），没有超时的话
    /// 宿主会永远卡在等待 Worker 响应上，托盘菜单随之停止刷新。</para>
    /// <para>取值依据（2026-10-27 修订）：插件的 poll 已改为「取走缓冲区后立即返回」，
    /// 毫秒级完成，不再有 12 秒扫描窗口，因此 20 秒的旧值失去依据、也过长——
    /// 超时过长会拖慢熔断（连续失败 3 次才触发）。实测单轮 poll 为毫秒级，
    /// 5 秒已是上千倍余量，同时把「Worker 卡死」的发现时间从 60 秒压到 15 秒。</para>
    /// <para>注意：Worker 启动后<b>不做阻塞式预热</b>，首个 poll 不会被拖慢；
    /// 若将来重新引入启动预热，本值必须大于预热时长。</para>
    /// </remarks>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>发现请求超时（需覆盖设备枚举耗时）。</summary>
    public TimeSpan DiscoverTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>诊断请求超时。</summary>
    public TimeSpan DiagnosticsTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 按需主动读取的超时（<see cref="PluginSdk.PluginMethods.ActiveRead"/>）。
    /// </summary>
    /// <remarks>
    /// 必须显著长于 <see cref="PollTimeout"/>：主动读取要完成「SDP 解析 → 建链路 →
    /// 协议握手 → 注册链 → 短监听等推送 → 断开」整套动作（实测 SDP 解析可耗时数十秒），
    /// 而被动轮询只是扫一段广播窗口。
    /// 该值须由实测调优（§5.3）。
    /// </remarks>
    public TimeSpan ActiveReadTimeout { get; set; } = TimeSpan.FromSeconds(90);

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
