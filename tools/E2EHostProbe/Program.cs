using PeripheralBattery.Core;
using PeripheralBattery.PluginHost;

namespace PeripheralBattery.E2EHostProbe;

/// <summary>
/// 宿主链路端到端探针：复用宿主的插件发现与 Worker 生命周期代码，
/// 拉起真实插件 Worker 并取一次读数，验证「宿主 → PluginHost → Worker → 广播」整条链路。
/// </summary>
/// <remarks>
/// 用法：<c>dotnet run --project tools/E2EHostProbe -- --plugins-dir artifacts/plugins [--rounds N]</c>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var pluginsDirectory = ReadOption(args, "--plugins-dir")
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "plugins");

        var rounds = 1;
        if (ReadOption(args, "--rounds") is { } roundsText && int.TryParse(roundsText, out var parsed))
        {
            rounds = Math.Max(1, parsed);
        }

        pluginsDirectory = Path.GetFullPath(pluginsDirectory);
        Console.WriteLine($"插件目录：{pluginsDirectory}");

        var options = new PluginHostOptions();
        var discovery = PluginCatalog.Discover(pluginsDirectory, options);

        if (discovery.Rejected.Count > 0)
        {
            Console.WriteLine("--- 被拒绝的插件 ---");
            foreach (var (source, reason) in discovery.Rejected)
            {
                Console.WriteLine($"  {source}: {reason}");
            }
        }

        if (discovery.Plugins.Count == 0)
        {
            Console.Error.WriteLine("未发现任何可用插件，端到端验证无法进行。");
            return 2;
        }

        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        var hosts = new List<PluginWorkerHost>();

        foreach (var descriptor in discovery.Plugins)
        {
            Console.WriteLine($"已发现插件：{descriptor.Id}（{descriptor.Name} v{descriptor.Manifest.Version}，"
                + $"验证状态 {descriptor.Manifest.Verification.Status}）");
            hosts.Add(new PluginWorkerHost(descriptor, options, log: m => Console.WriteLine($"  [log] {m}")));
        }

        var exitCode = 0;
        try
        {
            for (var round = 1; round <= rounds; round++)
            {
                Console.WriteLine($"\n=== 第 {round}/{rounds} 轮轮询 ===");

                foreach (var host in hosts)
                {
                    var outcome = await host.PollAsync(CancellationToken.None);

                    Console.WriteLine($"插件 {host.PluginId}: Ok={outcome.Ok} 健康={host.Health} "
                        + $"连续失败={host.ConsecutiveFailures}");

                    if (!outcome.Ok)
                    {
                        Console.WriteLine($"  错误 {outcome.ErrorCode}: {outcome.ErrorMessage}");
                        exitCode = 3;
                        continue;
                    }

                    var changed = store.Apply(outcome.Readings, DateTimeOffset.UtcNow);
                    Console.WriteLine($"  读数 {outcome.Readings.Count} 条，状态变化={changed}");

                    foreach (var reading in outcome.Readings)
                    {
                        Console.WriteLine(
                            $"  - {reading.Component,-14} 电量={(reading.Percentage?.ToString() ?? "未知"),-4} "
                            + $"质量={reading.Quality,-11} 充电={reading.IsCharging?.ToString() ?? "-"} "
                            + $"连接={reading.IsConnected?.ToString() ?? "-"} 来源={reading.Source} "
                            + $"诊断={reading.DiagnosticCode ?? "-"}");
                    }
                }
            }

            Console.WriteLine("\n=== 设备快照（宿主归并后的结果）===");
            var devices = store.GetDevices();
            if (devices.Count == 0)
            {
                Console.WriteLine("(无设备)");
                exitCode = 4;
            }

            foreach (var device in devices)
            {
                Console.WriteLine($"{device.DeviceName} [{device.DeviceId}] 插件={device.PluginId} "
                    + $"最低电量={device.LowestReportedPercentage?.ToString() ?? "未知"}");

                foreach (var component in device.Components.Values.OrderBy(c => c.Component))
                {
                    Console.WriteLine($"    {component.Component,-14} {component.Percentage?.ToString() ?? "未知",-4} "
                        + $"{component.Quality}");
                }
            }
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.StopAsync();
                await host.DisposeAsync();
            }
        }

        return exitCode;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }
}
