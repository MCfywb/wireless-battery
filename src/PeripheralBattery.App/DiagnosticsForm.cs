using PeripheralBattery.Core;
using PeripheralBattery.PluginHost;

namespace PeripheralBattery.App;

/// <summary>诊断窗口：展示读数快照、诊断日志与 Worker stderr。</summary>
public sealed class DiagnosticsForm : Form
{
    private readonly DeviceStateStore _store;
    private readonly List<PluginWorkerHost> _hosts;

    /// <summary>构造诊断窗口。</summary>
    public DiagnosticsForm(DeviceStateStore store, List<PluginWorkerHost> hosts)
    {
        _store = store;
        _hosts = [.. hosts];

        Text = "诊断";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 560);

        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        var copy = new Button { Text = "复制全部", Width = 100, Height = 28 };
        var refresh = new Button { Text = "刷新", Width = 90, Height = 28 };
        var close = new Button { Text = "关闭", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };

        copy.Click += (_, _) => Clipboard.SetText(text.Text);
        refresh.Click += (_, _) => Populate(text);
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(10),
        };
        buttons.Controls.Add(copy);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(close);

        Controls.Add(text);
        Controls.Add(buttons);
        CancelButton = close;

        Populate(text);
    }

    private void Populate(TextBox target)
    {
        var now = DateTimeOffset.UtcNow;
        var builder = new System.Text.StringBuilder();

        builder.AppendLine($"=== 设备读数（{now:O}）===");
        var devices = _store.GetDevices();
        if (devices.Count == 0)
        {
            builder.AppendLine("(无)");
        }

        foreach (var device in devices)
        {
            builder.AppendLine($"{device.DeviceName}  [{device.DeviceId}]  插件={device.PluginId}");
            foreach (var component in device.Components.Values.OrderBy(c => c.Component))
            {
                builder.AppendLine(
                    $"  {ReadingFormatter.ComponentName(component.Component),-8} " +
                    $"{ReadingFormatter.Format(component, now),-28} " +
                    $"质量={component.Quality} 来源={component.Source} 诊断={component.DiagnosticCode ?? "-"}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("=== 插件运行时 ===");
        if (_hosts.Count == 0)
        {
            builder.AppendLine("(无已加载插件)");
        }

        foreach (var host in _hosts)
        {
            builder.AppendLine(
                $"{host.PluginId}: 健康={host.Health} 连续失败={host.ConsecutiveFailures} 最近错误={host.LastError ?? "-"}");
        }

        builder.AppendLine();
        builder.AppendLine("=== 诊断日志 ===");
        var diagnostics = _store.GetDiagnostics();
        if (diagnostics.Count == 0)
        {
            builder.AppendLine("(空)");
        }
        else
        {
            foreach (var line in diagnostics)
            {
                builder.AppendLine(line);
            }
        }

        foreach (var host in _hosts)
        {
            var stderr = host.StderrLines;
            if (stderr.Count == 0)
            {
                continue;
            }

            builder.AppendLine();
            builder.AppendLine($"=== {host.PluginId} stderr ===");
            foreach (var line in stderr)
            {
                builder.AppendLine(line);
            }
        }

        target.Text = builder.ToString();
        target.SelectionStart = 0;
        target.SelectionLength = 0;
    }
}
