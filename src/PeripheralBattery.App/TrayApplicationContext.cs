using PeripheralBattery.Core;
using PeripheralBattery.PluginHost;

namespace PeripheralBattery.App;

/// <summary>
/// 托盘宿主上下文：维护 NotifyIcon、动态设备菜单与各窗口。
/// </summary>
/// <remarks>
/// <para>实现 docs/development-plan.md §6.1/§6.2：</para>
/// <list type="bullet">
/// <item><description>设备项动态创建，不硬编码具体型号。</description></item>
/// <item><description>退出时先停止刷新、再关闭 Worker、最后释放句柄再退出进程。</description></item>
/// <item><description>所有设备 IO 在后台执行，UI 线程只读缓存快照。</description></item>
/// </list>
/// </remarks>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusHeader;
    private readonly ToolStripMenuItem _devicesHeader;
    private readonly ToolStripSeparator _separator;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _pluginsItem;
    private readonly ToolStripMenuItem _diagnosticsItem;
    private readonly ToolStripMenuItem _exitItem;

    /// <summary>
    /// 隐藏的封送控件：<see cref="Control.BeginInvoke"/> 会回到创建它的线程（UI 线程），
    /// 用于把后台轮询结果安全地转到 UI 线程更新菜单（§6.4 不得阻塞 UI 线程）。
    /// </summary>
    private readonly Control _uiDispatcher;

    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly DeviceStateStore _store;
    private readonly List<PluginWorkerHost> _pluginHosts = [];
    private readonly List<string> _pluginRejections = [];
    private MonitoringScheduler? _scheduler;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private bool _shuttingDown;
    private DateTimeOffset _lastLowBatteryNotifyUtc = DateTimeOffset.MinValue;

    /// <summary>托盘里已创建的设备条目；条目常驻，只就地更新文本。</summary>
    private readonly Dictionary<string, DeviceMenuEntry> _deviceEntries = new(StringComparer.Ordinal);

    /// <summary>「尚未读取到设备」占位条目。</summary>
    private ToolStripMenuItem? _emptyDeviceItem;

    /// <summary>设备集合签名；变化时整块重建。</summary>
    private string _deviceSignature = string.Empty;

    /// <summary>菜单当前是否处于展开状态。</summary>
    private bool _menuOpen;

    /// <summary>菜单展开期间发生的结构变化，待菜单关闭后补做。</summary>
    private bool _pendingMenuRefresh;

    /// <summary>构造托盘上下文。</summary>
    public TrayApplicationContext(AppSettings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;

        _store = new DeviceStateStore(TimeSpan.FromSeconds(settings.StaleAfterSeconds));

        _statusHeader = new ToolStripMenuItem("正在初始化…") { Enabled = false };
        _devicesHeader = new ToolStripMenuItem("设备") { Enabled = false };
        _separator = new ToolStripSeparator();
        _refreshItem = new ToolStripMenuItem("立即刷新", null, async (_, _) => await RefreshNowAsync());
        _settingsItem = new ToolStripMenuItem("设置…", null, (_, _) => ShowSettings());
        _pluginsItem = new ToolStripMenuItem("插件管理…", null, (_, _) => ShowPluginManager());
        _diagnosticsItem = new ToolStripMenuItem("诊断…", null, (_, _) => ShowDiagnostics());
        _exitItem = new ToolStripMenuItem("退出", null, async (_, _) => await ShutdownAsync());

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusHeader);
        _menu.Items.Add(_separator);
        _menu.Items.Add(_devicesHeader);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_refreshItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_pluginsItem);
        _menu.Items.Add(_diagnosticsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Peripheral Battery Monitor",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusSummary();

        // 菜单展开期间禁止重建条目：销毁正在显示的条目会让下拉直接收起，
        // 用户看到的是「菜单隔几秒闪一下、要点两次设备」。结构变化推迟到关闭后补做。
        _menu.Opening += (_, _) => _menuOpen = true;
        _menu.Closed += (_, _) => OnMenuClosed();

        // 强制创建句柄，保证 BeginInvoke 可用。
        _uiDispatcher = new Control();
        _ = _uiDispatcher.Handle;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => RefreshMenu();
    }

    /// <summary>启动后台轮询（不阻塞 UI 线程）。</summary>
    public void Start()
    {
        var pluginsDirectory = string.IsNullOrWhiteSpace(_settings.PluginsDirectory)
            ? PluginCatalog.DefaultPluginsDirectory
            : _settings.PluginsDirectory;

        var options = new PluginHostOptions();
        var discovery = PluginCatalog.Discover(pluginsDirectory, options);
        _pluginRejections.Clear();
        _pluginRejections.AddRange(discovery.Rejected.Select(r => $"{r.Source}: {r.Reason}"));

        foreach (var descriptor in discovery.Plugins)
        {
            if (_settings.DisabledPlugins.Contains(descriptor.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _pluginHosts.Add(new PluginWorkerHost(descriptor, options, log: m => _store.AddDiagnostic(m)));
        }

        if (_pluginHosts.Count == 0)
        {
            _store.AddDiagnostic($"未发现可用插件（目录：{pluginsDirectory}）");
        }

        var clients = _pluginHosts.Cast<IPluginClient>().ToList();
        _scheduler = new MonitoringScheduler(
            clients,
            _store,
            TimeSpan.FromSeconds(_settings.PollIntervalSeconds),
            log: m => _store.AddDiagnostic(m));

        // 调度器在后台线程触发事件，必须封送到 UI 线程再改菜单（§6.4）。
        _scheduler.StateChanged += (_, _) => PostToUi(RefreshMenu);
        _scheduler.Start();

        _uiTimer.Start();
    }

    /// <summary>获取当前插件运行时列表（供插件管理窗口使用）。</summary>
    public IReadOnlyList<PluginWorkerHost> PluginHosts => _pluginHosts;

    /// <summary>获取插件加载失败原因。</summary>
    public IReadOnlyList<string> PluginRejections => _pluginRejections;

    /// <summary>获取状态存储。</summary>
    public DeviceStateStore Store => _store;

    /// <summary>获取当前设置。</summary>
    public AppSettings Settings => _settings;

    /// <summary>立即刷新全部插件。</summary>
    public async Task RefreshNowAsync()
    {
        if (_scheduler is not null)
        {
            try
            {
                await _scheduler.RefreshNowAsync();
            }
            catch (Exception ex)
            {
                _store.AddDiagnostic($"刷新异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // 可能在后台线程被调用，统一封送回 UI 线程刷新菜单。
        PostToUi(RefreshMenu);
    }

    private void RefreshMenu()
    {
        var now = DateTimeOffset.UtcNow;
        var devices = _store.GetDevices();

        // 设备条目常驻、只就地更新文本：早期实现每秒销毁重建整块条目，
        // 会把正在展开的下拉一起销毁，表现为「菜单隔几秒闪一下、要点两次设备」。
        SyncDeviceItems(devices, now);

        var lowest = devices.Select(d => d.LowestReportedPercentage).Where(p => p is not null).ToList();
        var lowestValue = lowest.Count > 0 ? lowest.Min() : null;

        _statusHeader.Text = devices.Count == 0
            ? "尚未读取到设备"
            : $"共 {devices.Count} 台设备"
              + (lowestValue is { } lv ? $"  最低电量 {lv}%" : string.Empty);

        _notifyIcon.Text = BuildTooltip(devices, now);
        _notifyIcon.Icon = BuildIcon(lowestValue);

        CheckLowBattery(devices, now);
    }

    /// <summary>
    /// 菜单关闭：补做展开期间被推迟的结构变化。
    /// </summary>
    private void OnMenuClosed()
    {
        _menuOpen = false;
        if (_pendingMenuRefresh)
        {
            _pendingMenuRefresh = false;
            RefreshMenu();
        }
    }

    /// <summary>
    /// 同步托盘里的设备条目：条目常驻，文本就地更新；结构变化（设备增删、部件增删）才重建。
    /// </summary>
    /// <remarks>
    /// 菜单展开期间一律不重建：销毁正在显示的条目会让下拉直接收起，用户必须重新点击。
    /// 结构变化推迟到菜单关闭后再补做（<see cref="_pendingMenuRefresh"/>）。
    /// </remarks>
    private void SyncDeviceItems(IReadOnlyList<DeviceSnapshot> devices, DateTimeOffset now)
    {
        var deviceSignature = string.Join("\u001f", devices.Select(d => d.DeviceId));
        if (!string.Equals(deviceSignature, _deviceSignature, StringComparison.Ordinal))
        {
            if (_menuOpen)
            {
                _pendingMenuRefresh = true;
                return;
            }

            RebuildDeviceBlock(devices, now);
            _deviceSignature = deviceSignature;
            return;
        }

        foreach (var device in devices)
        {
            if (!_deviceEntries.TryGetValue(device.DeviceId, out var entry))
            {
                continue;
            }

            if (!string.Equals(entry.Shape, BuildShape(device), StringComparison.Ordinal))
            {
                if (_menuOpen)
                {
                    _pendingMenuRefresh = true;
                    return;
                }

                RebuildDeviceEntry(entry, device, now);
                continue;
            }

            UpdateDeviceTexts(entry, device, now);
        }
    }

    /// <summary>设备集合变化时整块重建（少见：设备出现或消失）。</summary>
    private void RebuildDeviceBlock(IReadOnlyList<DeviceSnapshot> devices, DateTimeOffset now)
    {
        foreach (var entry in _deviceEntries.Values)
        {
            _menu.Items.Remove(entry.Root);
            entry.Root.Dispose();
        }
        _deviceEntries.Clear();

        if (_emptyDeviceItem is not null)
        {
            _menu.Items.Remove(_emptyDeviceItem);
            _emptyDeviceItem.Dispose();
            _emptyDeviceItem = null;
        }

        if (devices.Count == 0)
        {
            _emptyDeviceItem = new ToolStripMenuItem("尚未读取到设备") { Enabled = false };
            _menu.Items.Insert(_menu.Items.IndexOf(_devicesHeader) + 1, _emptyDeviceItem);
            return;
        }

        var insertIndex = _menu.Items.IndexOf(_devicesHeader) + 1;
        foreach (var device in devices)
        {
            var entry = CreateDeviceEntry(device, now);
            _deviceEntries[device.DeviceId] = entry;
            _menu.Items.Insert(insertIndex, entry.Root);
            insertIndex++;
        }
    }

    /// <summary>创建一台设备的菜单条目。</summary>
    private DeviceMenuEntry CreateDeviceEntry(DeviceSnapshot device, DateTimeOffset now)
    {
        // 设备条目必须 Enabled：禁用会使整棵子树无法点击，
        // 用户看到的是「灰的、不能选中」。子项里的数值仍是只读标签。
        var root = new ToolStripMenuItem(device.DeviceName)
        {
            Enabled = true,
            Tag = "device",
        };

        // 详情按设备 id 现查，避免闭包捕获到创建时的旧快照。
        var deviceId = device.DeviceId;
        var detailItem = new ToolStripMenuItem("查看详情…") { Tag = "detail" };
        detailItem.Click += (_, _) => ShowDeviceDetail(deviceId);
        root.DropDownItems.Add(detailItem);
        root.DropDownItems.Add(new ToolStripSeparator());

        var entry = new DeviceMenuEntry(root);
        RebuildDeviceEntry(entry, device, now);
        return entry;
    }

    /// <summary>重建某台设备的子项（部件行与首末信息行）。</summary>
    private void RebuildDeviceEntry(DeviceMenuEntry entry, DeviceSnapshot device, DateTimeOffset now)
    {
        var root = entry.Root;

        // 只清掉部件行与信息行，保留「查看详情…」与分隔线。
        for (var i = root.DropDownItems.Count - 1; i >= 0; i--)
        {
            var child = root.DropDownItems[i];
            if (child is ToolStripSeparator || child.Tag as string == "detail")
            {
                continue;
            }

            root.DropDownItems.RemoveAt(i);
            child.Dispose();
        }

        entry.Components.Clear();
        entry.LastUpdate = null;
        entry.Error = null;
        entry.Shape = BuildShape(device);

        foreach (var component in device.Components.Values.OrderBy(c => c.Component))
        {
            var item = new ToolStripMenuItem(
                $"{ReadingFormatter.ComponentName(component.Component)}  {ReadingFormatter.Format(component, now)}")
            {
                Enabled = false,
                Tag = "component",
            };
            root.DropDownItems.Add(item);
            entry.Components[component.Component] = item;
        }

        if (device.LastSuccessfulReadUtc is { } last)
        {
            entry.LastUpdate = new ToolStripMenuItem($"最后更新：{FormatTimestamp(last)}")
            {
                Enabled = false,
                Tag = "info",
            };
            root.DropDownItems.Add(entry.LastUpdate);
        }

        if (device.LastErrorCode is { } error)
        {
            entry.Error = new ToolStripMenuItem($"错误：{error}")
            {
                Enabled = false,
                Tag = "info",
            };
            root.DropDownItems.Add(entry.Error);
        }
    }

    /// <summary>只更新文本，不动条目结构（常规路径）。</summary>
    private void UpdateDeviceTexts(DeviceMenuEntry entry, DeviceSnapshot device, DateTimeOffset now)
    {
        entry.Root.Text = device.DeviceName;

        foreach (var component in device.Components.Values.OrderBy(c => c.Component))
        {
            if (entry.Components.TryGetValue(component.Component, out var item))
            {
                item.Text = $"{ReadingFormatter.ComponentName(component.Component)}  {ReadingFormatter.Format(component, now)}";
            }
        }

        if (entry.LastUpdate is not null && device.LastSuccessfulReadUtc is { } last)
        {
            entry.LastUpdate.Text = $"最后更新：{FormatTimestamp(last)}";
        }

        if (entry.Error is not null && device.LastErrorCode is { } error)
        {
            entry.Error.Text = $"错误：{error}";
        }
    }

    /// <summary>子项形状签名：部件集合与首末信息行是否存在。</summary>
    private static string BuildShape(DeviceSnapshot device)
        => string.Join("\u001f", device.Components.Values.OrderBy(c => c.Component).Select(c => c.Component))
            + (device.LastSuccessfulReadUtc is not null ? "\u001flu" : "")
            + (device.LastErrorCode is not null ? "\u001ferr" : "");

    /// <summary>
    /// 最后更新时间用绝对时刻。
    /// </summary>
    /// <remarks>
    /// 相对时间（「N 秒前」）每秒都变，会让菜单文本持续抖动；
    /// 绝对时刻只在读到新数据时才变。
    /// </remarks>
    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToLocalTime().ToString("HH:mm:ss");

    private string BuildTooltip(IReadOnlyList<DeviceSnapshot> devices, DateTimeOffset now)
    {
        if (devices.Count == 0)
        {
            return "Peripheral Battery Monitor";
        }

        var lines = devices.Select(d =>
        {
            var parts = d.Components.Values
                .OrderBy(c => c.Component)
                .Select(c => $"{ReadingFormatter.ComponentName(c.Component)} {ReadingFormatter.Format(c, now)}");
            return $"{d.DeviceName}: {string.Join(" / ", parts)}";
        });

        // NotifyIcon 文本上限 63 字符。
        var text = string.Join("\n", lines);
        return text.Length <= 63 ? text : text[..63];
    }

    private static Icon BuildIcon(int? lowestPercentage)
    {
        // 托盘图标统一使用系统通用图标；最低电量以菜单与提示文字呈现，
        // 避免自绘图标在高分屏下难以辨识（§6.2）。
        return SystemIcons.Application;
    }

    private void CheckLowBattery(IReadOnlyList<DeviceSnapshot> devices, DateTimeOffset now)
    {
        if (!_settings.LowBatteryNotificationEnabled)
        {
            return;
        }

        var cooldown = TimeSpan.FromSeconds(_settings.LowBatteryCooldownSeconds);
        if (now - _lastLowBatteryNotifyUtc < cooldown)
        {
            return;
        }

        foreach (var device in devices)
        {
            var lowest = device.LowestReportedPercentage;
            if (lowest is { } value && value <= _settings.LowBatteryThreshold)
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "低电量提醒",
                    $"{device.DeviceName} 最低电量 {value}%",
                    ToolTipIcon.Warning);
                _lastLowBatteryNotifyUtc = now;
                return;
            }
        }
    }

    private void ShowStatusSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var devices = _store.GetDevices();
        var lines = devices.Select(d =>
        {
            var parts = d.Components.Values
                .OrderBy(c => c.Component)
                .Select(c => $"{ReadingFormatter.ComponentName(c.Component)} {ReadingFormatter.Format(c, now)}");
            return $"{d.DeviceName}: {string.Join("  ", parts)}";
        });

        _notifyIcon.ShowBalloonTip(5000, "电量状态", string.Join("\n", lines), ToolTipIcon.Info);
    }

    /// <summary>弹出单台设备的电量详情气泡。</summary>
    /// <param name="deviceId">设备标识；点击时现查最新快照，不使用创建条目时的旧数据。</param>
    private void ShowDeviceDetail(string deviceId)
    {
        var device = _store.GetDevice(deviceId);
        if (device is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var parts = device.Components.Values
            .OrderBy(c => c.Component)
            .Select(c => $"{ReadingFormatter.ComponentName(c.Component)} {ReadingFormatter.Format(c, now)}");

        var body = new List<string> { string.Join(" / ", parts) };

        if (device.LastSuccessfulReadUtc is { } last)
        {
            body.Add($"最后更新：{ReadingFormatter.FormatAge(now - last)}");
        }

        if (device.LastErrorCode is { } error)
        {
            body.Add($"错误：{error}");
        }

        _notifyIcon.ShowBalloonTip(5000, device.DeviceName, string.Join("\n", body), ToolTipIcon.Info);
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _settings.PollIntervalSeconds = form.PollIntervalSeconds;
            _settings.StaleAfterSeconds = form.StaleAfterSeconds;
            _settings.LowBatteryThreshold = form.LowBatteryThreshold;
            _settings.LowBatteryNotificationEnabled = form.LowBatteryNotificationEnabled;
            _settings.Normalize();
            _settings.Save(_settingsPath);
            _store.AddDiagnostic("设置已保存；轮询间隔与过期时间在下次启动后生效。");
            RefreshMenu();
        }
    }

    private void ShowPluginManager()
    {
        using var form = new PluginManagerForm(_pluginHosts, _pluginRejections, _settings);
        form.ShowDialog();
        RefreshMenu();
    }

    private void ShowDiagnostics()
    {
        using var form = new DiagnosticsForm(_store, _pluginHosts);
        form.ShowDialog();
    }

    /// <summary>
    /// 把操作封送到 UI 线程执行。
    /// </summary>
    /// <remarks>后台轮询线程不得直接改动 WinForms 控件（§6.4）。</remarks>
    private void PostToUi(Action action)
    {
        try
        {
            if (_uiDispatcher.IsDisposed)
            {
                return;
            }

            _uiDispatcher.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 正在退出，忽略。
        }
        catch (InvalidOperationException)
        {
            // 句柄尚未创建或已销毁，忽略。
        }
    }

    /// <summary>
    /// 退出流程：先停止刷新与后台任务，再关闭 Worker，最后释放托盘句柄。
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _uiTimer.Stop();

        if (_scheduler is not null)
        {
            await _scheduler.StopAsync().ConfigureAwait(false);
            await _scheduler.DisposeAsync().ConfigureAwait(false);
            _scheduler = null;
        }

        foreach (var host in _pluginHosts)
        {
            try
            {
                await host.StopAsync();
                await host.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // 关闭 Worker 过程中的异常不应阻止进程退出。
            }
        }

        _pluginHosts.Clear();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();

        ExitThread();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_shuttingDown)
        {
            _shuttingDown = true;
            _uiTimer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _uiTimer.Dispose();
            _deviceEntries.Clear();
            _emptyDeviceItem = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 托盘里一台设备的菜单条目集合。
    /// </summary>
    /// <remarks>
    /// 条目只创建一次，之后就地更新文本：反复销毁重建会让菜单闪烁，
    /// 正在展开的下拉被关闭，用户必须重新点击设备。
    /// </remarks>
    private sealed class DeviceMenuEntry
    {
        /// <summary>构造条目集合。</summary>
        public DeviceMenuEntry(ToolStripMenuItem root) => Root = root;

        /// <summary>设备一级菜单条目。</summary>
        public ToolStripMenuItem Root { get; }

        /// <summary>部件行，按部件名索引。</summary>
        public Dictionary<PluginSdk.BatteryComponent, ToolStripMenuItem> Components { get; } = new();

        /// <summary>「最后更新」行；不存在时为 null。</summary>
        public ToolStripMenuItem? LastUpdate { get; set; }

        /// <summary>「错误」行；不存在时为 null。</summary>
        public ToolStripMenuItem? Error { get; set; }

        /// <summary>子项形状签名，用于判断是否需要重建子项。</summary>
        public string Shape { get; set; } = string.Empty;
    }
}
