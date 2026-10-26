using System.Windows.Forms;

namespace PeripheralBattery.App;

/// <summary>应用入口。</summary>
internal static class Program
{
    /// <summary>单实例互斥体名称；按用户隔离，避免多用户互相阻塞。</summary>
    private static string MutexName => $"Local\\PeripheralBatteryMonitor-{Environment.UserName}-{Environment.UserDomainName}";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 第二次启动：提示已有实例（§6.2）。
            MessageBox.Show(
                "Peripheral Battery Monitor 已在运行，请从系统托盘打开菜单。",
                "Peripheral Battery Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var settingsPath = AppSettings.DefaultConfigPath;
        var settings = AppSettings.Load(settingsPath);

        var context = new TrayApplicationContext(settings, settingsPath);
        try
        {
            // 启动只做插件发现与调度器拉起，不等待首轮轮询，避免 UI 线程被阻塞。
            context.Start();
            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动失败：{ex.GetType().Name}: {ex.Message}",
                "Peripheral Battery Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            context.Dispose();
        }
    }
}
