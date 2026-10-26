using System.Text.Json;
using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.PluginHost;

/// <summary>已安装插件的描述信息。</summary>
public sealed class PluginDescriptor
{
    /// <summary>插件清单。</summary>
    public required PluginManifest Manifest { get; init; }

    /// <summary>插件根目录（含 manifest.json）。</summary>
    public required string PluginRootDirectory { get; init; }

    /// <summary>Worker 可执行文件的绝对路径。</summary>
    public required string WorkerExecutablePath { get; init; }

    /// <summary>插件 id。</summary>
    public string Id => Manifest.Id;

    /// <summary>插件显示名。</summary>
    public string Name => Manifest.Name;
}

/// <summary>插件发现结果。</summary>
/// <param name="Plugins">通过校验的插件。</param>
/// <param name="Rejected">被拒绝的插件及原因（重复 id、清单非法、路径越界等）。</param>
public sealed record PluginDiscoveryResult(
    IReadOnlyList<PluginDescriptor> Plugins,
    IReadOnlyList<(string Source, string Reason)> Rejected);

/// <summary>
/// 插件目录扫描与清单校验。
/// </summary>
/// <remarks>
/// <para>实现 docs/development-plan.md §5.1/§5.2：插件位于
/// <c>%LocalAppData%/PeripheralBatteryMonitor/plugins/&lt;plugin-id&gt;/</c>；
/// 安装前校验 manifest，拒绝重复 id、非法 worker 路径与目录跳出。</para>
/// </remarks>
public static class PluginCatalog
{
    /// <summary>默认插件目录。</summary>
    public static string DefaultPluginsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeripheralBatteryMonitor",
        "plugins");

    /// <summary>扫描插件目录。</summary>
    /// <param name="pluginsDirectory">插件根目录；不存在时返回空结果。</param>
    /// <param name="options">主机配置。</param>
    public static PluginDiscoveryResult Discover(string pluginsDirectory, PluginHostOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var plugins = new List<PluginDescriptor>();
        var rejected = new List<(string, string)>();

        if (!Directory.Exists(pluginsDirectory))
        {
            return new PluginDiscoveryResult(plugins, rejected);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directory in Directory.EnumerateDirectories(pluginsDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                rejected.Add((directory, "缺少 manifest.json"));
                continue;
            }

            PluginManifest? manifest;
            try
            {
                var info = new FileInfo(manifestPath);
                if (info.Length > PluginManifestValidator.MaxManifestBytes)
                {
                    rejected.Add((directory, $"manifest.json 超过 {PluginManifestValidator.MaxManifestBytes} 字节上限"));
                    continue;
                }

                manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                rejected.Add((directory, $"manifest.json 解析失败：{ex.Message}"));
                continue;
            }

            if (manifest is null)
            {
                rejected.Add((directory, "manifest.json 内容为空"));
                continue;
            }

            var validation = PluginManifestValidator.Validate(manifest, directory);
            if (!validation.IsValid)
            {
                rejected.Add((directory, string.Join("；", validation.Errors)));
                continue;
            }

            if (!seenIds.Add(manifest.Id))
            {
                rejected.Add((directory, $"重复的插件 id：{manifest.Id}"));
                continue;
            }

            var workerPath = Path.GetFullPath(Path.Combine(directory, manifest.Worker));
            if (!File.Exists(workerPath))
            {
                rejected.Add((directory, $"Worker 可执行文件不存在：{manifest.Worker}"));
                continue;
            }

            plugins.Add(new PluginDescriptor
            {
                Manifest = manifest,
                PluginRootDirectory = Path.GetFullPath(directory),
                WorkerExecutablePath = workerPath,
            });
        }

        return new PluginDiscoveryResult(plugins, rejected);
    }
}
