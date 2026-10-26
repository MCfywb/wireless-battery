using System.Reflection;
using System.Windows.Forms;
using PeripheralBattery.App;
using PeripheralBattery.Core;
using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.App.Tests;

/// <summary>
/// 托盘菜单测试。
/// </summary>
/// <remarks>
/// <para>覆盖真机验收暴露的两个缺陷：</para>
/// <list type="number">
/// <item><description>
/// 菜单条目曾每秒被销毁重建，正在展开的下拉随之关闭，
/// 表现为「菜单隔几秒闪一下、要点两次设备」。此处锁定「条目实例常驻」。
/// </description></item>
/// <item><description>
/// 设备条目曾被设为 <c>Enabled = false</c>，整棵子树不可点击，
/// 表现为「灰的、不能选中」。此处锁定设备条目可点击。
/// </description></item>
/// </list>
/// <para>白盒测试：通过反射访问 <c>TrayApplicationContext</c> 的私有菜单与刷新方法。</para>
/// </remarks>
public sealed class TrayMenuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>WinForms 控件必须在 STA 线程上创建。</summary>
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }

    [Fact]
    public void 设备条目必须可点击且带部件明细()
    {
        RunSta(() =>
        {
            using var context = NewContext();
            Apply(context, "bt:AA", "vivo TWS 3i", 90);
            Refresh(context);

            var items = DeviceItems(context);
            var device = Assert.Single(items);

            Assert.True(device.Enabled);
            Assert.Equal("vivo TWS 3i", device.Text);

            // 子项顺序：查看详情… → 分隔线 → 三个部件 → 最后更新
            Assert.Equal(6, device.DropDownItems.Count);
            Assert.Equal("查看详情…", device.DropDownItems[0].Text);
            Assert.IsType<ToolStripSeparator>(device.DropDownItems[1]);
            Assert.Equal("左耳  90%", device.DropDownItems[2].Text);
            Assert.Equal("右耳  90%", device.DropDownItems[3].Text);
            Assert.Equal("充电盒  90%", device.DropDownItems[4].Text);
            Assert.StartsWith("最后更新：", device.DropDownItems[5].Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 数值变化后设备条目实例必须复用()
    {
        // 回归：早期实现每次刷新都销毁重建设备条目，会把正在展开的下拉一起销毁，
        // 用户看到菜单闪一下且必须重新点击设备。
        RunSta(() =>
        {
            using var context = NewContext();
            Apply(context, "bt:AA", "vivo TWS 3i", 90);
            Refresh(context);

            var before = Assert.Single(DeviceItems(context));

            Apply(context, "bt:AA", "vivo TWS 3i", 80, Now.AddSeconds(30));
            Refresh(context);

            var after = Assert.Single(DeviceItems(context));
            Assert.Same(before, after);
            Assert.Equal("左耳  80%", after.DropDownItems[2].Text);
        });
    }

    [Fact]
    public void 菜单展开期间结构变化必须推迟到关闭后()
    {
        RunSta(() =>
        {
            using var context = NewContext();
            Apply(context, "bt:AA", "vivo TWS 3i", 90);
            Refresh(context);

            Assert.Single(DeviceItems(context));

            // 菜单处于展开状态：新增设备不得立刻改结构。
            SetField(context, "_menuOpen", true);
            Apply(context, "bt:BB", "第二台设备", 70);
            Refresh(context);

            Assert.Single(DeviceItems(context));

            // 菜单关闭后补做。
            SetField(context, "_menuOpen", false);
            Invoke(context, "OnMenuClosed");

            Assert.Equal(2, DeviceItems(context).Count);
        });
    }

    private static TrayApplicationContext NewContext()
        => new(AppSettings.CreateDefault(), Path.Combine(Path.GetTempPath(), "app-tests-settings.json"));

    private static void Apply(
        TrayApplicationContext context,
        string deviceId,
        string name,
        int percent,
        DateTimeOffset? at = null)
    {
        var when = at ?? Now;
        context.Store.Apply(
        [
            new BatteryReading(deviceId, name, "probe-plugin", BatteryComponent.LeftEarbud, percent, false, true,
                ReadingQuality.Reported, when, when, "probe"),
            new BatteryReading(deviceId, name, "probe-plugin", BatteryComponent.RightEarbud, percent, false, true,
                ReadingQuality.Reported, when, when, "probe"),
            new BatteryReading(deviceId, name, "probe-plugin", BatteryComponent.ChargingCase, percent, false, true,
                ReadingQuality.Reported, when, when, "probe"),
        ], when);
    }

    private static List<ToolStripMenuItem> DeviceItems(TrayApplicationContext context)
    {
        var menu = (ContextMenuStrip)Field(context, "_menu")!;
        return menu.Items.OfType<ToolStripMenuItem>()
            .Where(i => i.Tag as string == "device")
            .ToList();
    }

    private static void Refresh(TrayApplicationContext context)
        => Invoke(context, "RefreshMenu");

    private static void Invoke(object target, string name)
        => target.GetType().GetMethod(name, Private)!.Invoke(target, null);

    private static object? Field(object target, string name)
        => target.GetType().GetField(name, Private)!.GetValue(target);

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, Private)!.SetValue(target, value);
}
