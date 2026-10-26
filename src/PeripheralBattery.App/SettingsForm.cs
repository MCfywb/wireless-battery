namespace PeripheralBattery.App;

/// <summary>设置窗口。实现 docs/development-plan.md §6.3 的通用设置项。</summary>
public sealed class SettingsForm : Form
{
    private readonly NumericUpDown _pollInterval = new() { Minimum = 5, Maximum = 3600, Increment = 5 };
    private readonly NumericUpDown _staleAfter = new() { Minimum = 30, Maximum = 86400, Increment = 30 };
    private readonly NumericUpDown _lowBattery = new() { Minimum = 1, Maximum = 100 };
    private readonly CheckBox _lowBatteryNotify = new() { Text = "启用低电量通知", AutoSize = true };

    /// <summary>构造设置窗口。</summary>
    public SettingsForm(AppSettings settings)
    {
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(420, 230);

        _pollInterval.Value = settings.PollIntervalSeconds;
        _staleAfter.Value = settings.StaleAfterSeconds;
        _lowBattery.Value = settings.LowBatteryThreshold;
        _lowBatteryNotify.Checked = settings.LowBatteryNotificationEnabled;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "轮询间隔（秒）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_pollInterval, 1, 0);
        layout.Controls.Add(new Label { Text = "读数过期时间（秒）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_staleAfter, 1, 1);
        layout.Controls.Add(new Label { Text = "低电量阈值（%）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_lowBattery, 1, 2);
        layout.Controls.Add(_lowBatteryNotify, 1, 3);

        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(12),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>轮询间隔（秒）。</summary>
    public int PollIntervalSeconds => (int)_pollInterval.Value;

    /// <summary>读数过期时间（秒）。</summary>
    public int StaleAfterSeconds => (int)_staleAfter.Value;

    /// <summary>低电量阈值（%）。</summary>
    public int LowBatteryThreshold => (int)_lowBattery.Value;

    /// <summary>是否启用低电量通知。</summary>
    public bool LowBatteryNotificationEnabled => _lowBatteryNotify.Checked;
}
