using PeripheralBattery.PluginHost;

namespace PeripheralBattery.App;

/// <summary>插件管理窗口。实现 docs/development-plan.md §5.4 的查看与启停能力。</summary>
public sealed class PluginManagerForm : Form
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
    };

    private readonly List<PluginWorkerHost> _hosts;
    private readonly AppSettings _settings;

    /// <summary>构造插件管理窗口。</summary>
    public PluginManagerForm(List<PluginWorkerHost> hosts, IReadOnlyList<string> rejections, AppSettings settings)
    {
        _hosts = [.. hosts];
        _settings = settings;

        Text = "插件管理";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 420);

        _list.Columns.Add("插件 id", 160);
        _list.Columns.Add("名称", 200);
        _list.Columns.Add("版本", 80);
        _list.Columns.Add("验证状态", 120);
        _list.Columns.Add("运行状态", 120);
        _list.Columns.Add("连续失败", 70);
        _list.Columns.Add("只读", 60);

        var restart = new Button { Text = "重启该插件", Width = 110, Height = 28 };
        var enable = new Button { Text = "启用/禁用", Width = 110, Height = 28 };
        var refresh = new Button { Text = "刷新列表", Width = 100, Height = 28 };
        var close = new Button { Text = "关闭", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };

        restart.Click += async (_, _) => await RestartSelectedAsync();
        enable.Click += (_, _) => ToggleSelected();
        refresh.Click += (_, _) => Populate();

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(10),
        };
        buttons.Controls.Add(restart);
        buttons.Controls.Add(enable);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(close);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 8, 10, 0),
            Text = rejections.Count == 0
                ? "未发现被拒绝的插件。"
                : $"以下插件未被加载：{string.Join("；", rejections)}",
        };

        root.Controls.Add(_list, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        Controls.Add(root);
        Controls.Add(header);
        AcceptButton = close;
        CancelButton = close;

        Populate();
    }

    private void Populate()
    {
        _list.Items.Clear();

        foreach (var host in _hosts)
        {
            var manifest = host.Descriptor.Manifest;
            var item = new ListViewItem(host.PluginId);
            item.SubItems.Add(manifest.Name);
            item.SubItems.Add(manifest.Version);
            item.SubItems.Add(manifest.Verification.Status);
            item.SubItems.Add(host.Health.ToString());
            item.SubItems.Add(host.ConsecutiveFailures.ToString());
            item.SubItems.Add(manifest.Capabilities.ReadOnly ? "是" : "否");
            item.Tag = host;
            _list.Items.Add(item);
        }
    }

    private PluginWorkerHost? SelectedHost
        => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as PluginWorkerHost : null;

    private async Task RestartSelectedAsync()
    {
        var host = SelectedHost;
        if (host is null)
        {
            return;
        }

        try
        {
            var ok = await host.RestartAsync(CancellationToken.None);
            MessageBox.Show(
                ok ? $"插件 {host.PluginId} 已重启。" : $"插件 {host.PluginId} 重启失败：{host.LastError}",
                "插件管理",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重启失败：{ex.Message}", "插件管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Populate();
    }

    private void ToggleSelected()
    {
        var host = SelectedHost;
        if (host is null)
        {
            return;
        }

        if (_settings.DisabledPlugins.Contains(host.PluginId, StringComparer.OrdinalIgnoreCase))
        {
            _settings.DisabledPlugins.RemoveAll(id => string.Equals(id, host.PluginId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            _settings.DisabledPlugins.Add(host.PluginId);
        }

        MessageBox.Show(
            "启用/禁用的改动将在下次启动后生效。",
            "插件管理",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
