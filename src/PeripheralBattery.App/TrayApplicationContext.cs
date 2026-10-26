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

        // 移除上一次的动态设备项。
        var dynamicItems = _menu.Items.OfType<ToolStripMenuItem>()
            .Where(i => i.Tag as string == "device")
            .ToList();
        foreach (var item in dynamicItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }

        var insertIndex = _menu.Items.IndexOf(_devicesHeader) + 1;
        var devicesHeaderIndex = insertIndex;

        if (devices.Count == 0)
        {
            var empty = new ToolStripMenuItem("尚未读取到设备") { Enabled = false, Tag = "device" };
            _menu.Items.Insert(insertIndex, empty);
        }
        else
        {
            foreach (var device in devices)
            {
                // 设备条目必须 Enabled：禁用会使整棵子树无法点击，
                // 用户看到的是「灰的、不能选中」。子项里的数值仍是只读标签。
                var deviceItem = new ToolStripMenuItem(device.DeviceName)
                {
                    Enabled = true,
                    Tag = "device",
                };

                var detailItem = new ToolStripMenuItem("查看详情…");
                detailItem.Click += (_, _) => ShowDeviceDetail(device);
                deviceItem.DropDownItems.Add(detailItem);
                deviceItem.DropDownItems.Add(new ToolStripSeparator());

                foreach (var component in device.Components.Values.OrderBy(c => c.Component))
                {
                    deviceItem.DropDownItems.Add(new ToolStripMenuItem(
                        $"{ReadingFormatter.ComponentName(component.Component)}  {ReadingFormatter.Format(component, now)}")
                    {
                        Enabled = false,
                    });
                }

                if (device.LastSuccessfulReadUtc is { } last)
                {
                    deviceItem.DropDownItems.Add(new ToolStripMenuItem(
                        $"最后更新：{ReadingFormatter.FormatAge(now - last)}")
                    {
                        Enabled = false,
                    });
                }

                if (device.LastErrorCode is { } error)
                {
                    deviceItem.DropDownItems.Add(new ToolStripMenuItem($"错误：{error}")
                    {
                        Enabled = false,
                    });
                }

                _menu.Items.Insert(insertIndex, deviceItem);
                insertIndex++;
            }
        }

        _ = devicesHeaderIndex;

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
    private void ShowDeviceDetail(DeviceSnapshot device)
    {
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
        }

        base.Dispose(disposing);
    }
}
