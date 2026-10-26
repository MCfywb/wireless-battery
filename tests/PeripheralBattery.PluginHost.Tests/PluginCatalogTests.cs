using System.Text.Json;
using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.PluginHost.Tests;

/// <summary>插件目录扫描与清单校验测试。</summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pbm-plugin-catalog-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }

    private void WritePlugin(string id, string workerRelative = "worker/worker.exe", bool createWorker = true, string? manifestJson = null)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(Path.Combine(dir, "worker"));

        if (createWorker)
        {
            File.WriteAllText(Path.Combine(dir, workerRelative.Replace('/', Path.DirectorySeparatorChar)), "fake");
        }

        if (manifestJson is null)
        {
            manifestJson = JsonSerializer.Serialize(new
            {
                manifestVersion = 1,
                id,
                name = $"插件 {id}",
                version = "0.1.0",
                minHostVersion = "0.1.0",
                worker = workerRelative,
                transports = new[] { "ble-advertisement" },
                deviceMatchers = new { modelIds = new[] { 132 } },
                capabilities = new { batteryComponents = new[] { "leftEarbud" }, readOnly = true },
                verification = new { status = "hardware-verified" },
            });
        }

        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);
    }

    [Fact]
    public void 应发现合法插件并解析worker绝对路径()
    {
        WritePlugin("vivo-tws-3i");

        var result = PluginCatalog.Discover(_root, new PluginHostOptions());

        Assert.Single(result.Plugins);
        var plugin = result.Plugins[0];
        Assert.Equal("vivo-tws-3i", plugin.Id);
        Assert.True(File.Exists(plugin.WorkerExecutablePath));
        Assert.StartsWith(Path.GetFullPath(_root), plugin.WorkerExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 重复插件id必须被拒绝()
    {
        WritePlugin("dup");
        var second = Path.Combine(_root, "dup2");
        Directory.CreateDirectory(Path.Combine(second, "worker"));
        File.WriteAllText(Path.Combine(second, "worker", "worker.exe"), "fake");
        File.WriteAllText(Path.Combine(second, "manifest.json"), File.ReadAllText(Path.Combine(_root, "dup", "manifest.json")));

        var result = PluginCatalog.Discover(_root, new PluginHostOptions());

        Assert.Single(result.Plugins);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public void worker路径跳出插件目录必须被拒绝()
    {
        var manifest = JsonSerializer.Serialize(new
        {
            manifestVersion = 1,
            id = "evil",
            name = "恶意插件",
            version = "0.1.0",
            minHostVersion = "0.1.0",
            worker = "../outside.exe",
            transports = new[] { "ble-advertisement" },
            deviceMatchers = new { modelIds = new[] { 1 } },
            capabilities = new { batteryComponents = new[] { "leftEarbud" }, readOnly = true },
            verification = new { status = "unknown" },
        });

        WritePlugin("evil", manifestJson: manifest);

        var result = PluginCatalog.Discover(_root, new PluginHostOptions());

        Assert.Empty(result.Plugins);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("..", StringComparison.Ordinal));
    }

    [Fact]
    public void 缺少manifest的目录必须被拒绝()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var result = PluginCatalog.Discover(_root, new PluginHostOptions());

        Assert.Empty(result.Plugins);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("manifest.json", StringComparison.Ordinal));
    }

    [Fact]
    public void worker文件不存在必须被拒绝()
    {
        WritePlugin("no-worker", createWorker: false);

        var result = PluginCatalog.Discover(_root, new PluginHostOptions());

        Assert.Empty(result.Plugins);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("不存在", StringComparison.Ordinal));
    }

    [Fact]
    public void 非法插件id必须被拒绝()
    {
        var manifest = JsonSerializer.Serialize(new
        {
            manifestVersion = 1,
            id = "Bad_ID!",
            name = "非法 id",
            version = "0.1.0",
            minHostVersion = "0.1.0",
            worker = "worker/worker.exe",
            transports = new[] { "ble-advertisement" },
            deviceMatchers = new { modelIds = new[] { 1 } },
            capabilities = new { batteryComponents = new[] { "leftEarbud" }, readOnly = true },
            verification = new { status = "unknown" },
        });

        WritePlugin("badid", manifestJson: manifest);

        var result = PluginCatalog.Discover(_root, new PluginHostOptions());

        Assert.Empty(result.Plugins);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("id 非法", StringComparison.Ordinal));
    }

    [Fact]
    public void 插件目录不存在时应返回空结果而非抛异常()
    {
        var result = PluginCatalog.Discover(Path.Combine(_root, "not-exist"), new PluginHostOptions());

        Assert.Empty(result.Plugins);
        Assert.Empty(result.Rejected);
    }
}
