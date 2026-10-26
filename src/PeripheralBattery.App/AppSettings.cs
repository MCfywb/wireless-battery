using System.Text.Json;

namespace PeripheralBattery.App;

/// <summary>应用配置。实现 docs/development-plan.md §6.3 的设置项。</summary>
public sealed class AppSettings
{
    /// <summary>当前配置结构版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>配置结构版本；不兼容变更时递增。</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>轮询间隔（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>读数过期时间（秒）；超过则显示为过期值。</summary>
    public int StaleAfterSeconds { get; set; } = 180;

    /// <summary>低电量通知阈值（百分比）。</summary>
    public int LowBatteryThreshold { get; set; } = 20;

    /// <summary>是否启用低电量通知。</summary>
    public bool LowBatteryNotificationEnabled { get; set; } = true;

    /// <summary>低电量通知冷却时间（秒）。</summary>
    public int LowBatteryCooldownSeconds { get; set; } = 1800;

    /// <summary>托盘图标是否显示数字覆盖。</summary>
    public bool ShowTrayNumbers { get; set; } = true;

    /// <summary>托盘图标模式：最低电量 / 仅状态 / 隐藏数值。</summary>
    public string TrayIconMode { get; set; } = "lowest";

    /// <summary>已禁用的插件 id。</summary>
    public List<string> DisabledPlugins { get; set; } = [];

    /// <summary>插件目录路径；留空时使用默认目录。</summary>
    public string? PluginsDirectory { get; set; }

    /// <summary>默认配置。</summary>
    public static AppSettings CreateDefault() => new();

    /// <summary>配置文件路径。</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeripheralBatteryMonitor",
        "settings.json");

    /// <summary>
    /// 读取配置；文件不存在或损坏时返回默认配置。
    /// </summary>
    /// <remarks>损坏的配置不应导致应用无法启动。</remarks>
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return CreateDefault();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (settings is null)
            {
                return CreateDefault();
            }

            if (settings.Version != CurrentVersion)
            {
                // 未来版本升级时在此迁移；当前仅有一个版本，版本不符则回退默认。
                return CreateDefault();
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return CreateDefault();
        }
    }

    /// <summary>
    /// 原子写入配置：先写临时文件再替换，避免写入中断产生半截文件。
    /// </summary>
    public void Save(string path)
    {
        Normalize();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>把数值约束到合理区间。</summary>
    public void Normalize()
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 5, 3600);
        StaleAfterSeconds = Math.Clamp(StaleAfterSeconds, 30, 86400);
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        LowBatteryCooldownSeconds = Math.Clamp(LowBatteryCooldownSeconds, 60, 86400);
        Version = CurrentVersion;
    }
}
