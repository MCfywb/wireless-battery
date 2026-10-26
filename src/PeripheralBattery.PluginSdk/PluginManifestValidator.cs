using System.Text.RegularExpressions;

namespace PeripheralBattery.PluginSdk;

/// <summary>manifest 校验结果。</summary>
public sealed record ManifestValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ManifestValidationResult Ok { get; } = new(true, Array.Empty<string>());
}

/// <summary>
/// manifest 校验器。规则取自 docs/development-plan.md §5.2：
/// <list type="bullet">
/// <item><description>id 仅允许小写字母、数字与连字符；不允许 <c>..</c>、绝对路径或目录跳出。</description></item>
/// <item><description>worker 规范化后必须位于插件目录内。</description></item>
/// <item><description>拒绝缺失字段、未知不兼容版本、过大的 JSON、外部符号链接与重复 plugin id。</description></item>
/// </list>
/// </summary>
public static partial class PluginManifestValidator
{
    /// <summary>允许的最大 manifest 文件字节数（防止过大的 JSON）。</summary>
    public const int MaxManifestBytes = 64 * 1024;

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    public static ManifestValidationResult Validate(PluginManifest manifest, string? pluginRootDirectory = null)
    {
        var errors = new List<string>();

        if (manifest.ManifestVersion != PluginManifest.CurrentManifestVersion)
        {
            errors.Add($"manifestVersion 不受支持：{manifest.ManifestVersion}（当前支持 {PluginManifest.CurrentManifestVersion}）");
        }

        if (!PluginIdPattern().IsMatch(manifest.Id))
        {
            errors.Add($"id 非法：\"{manifest.Id}\"（仅允许小写字母、数字与连字符）");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add("缺少 name");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add("缺少 version");
        }

        if (string.IsNullOrWhiteSpace(manifest.MinHostVersion))
        {
            errors.Add("缺少 minHostVersion");
        }

        if (string.IsNullOrWhiteSpace(manifest.Worker))
        {
            errors.Add("缺少 worker");
        }
        else if (!IsPathInsidePluginDirectory(manifest.Worker, pluginRootDirectory, out var pathError))
        {
            errors.Add(pathError);
        }

        if (manifest.Transports.Count == 0)
        {
            errors.Add("缺少 transports");
        }

        if (manifest.Capabilities.BatteryComponents.Count == 0)
        {
            errors.Add("缺少 capabilities.batteryComponents");
        }

        if (!VerificationStatuses.All.Contains(manifest.Verification.Status))
        {
            errors.Add($"verification.status 非法：\"{manifest.Verification.Status}\"");
        }

        return errors.Count == 0 ? ManifestValidationResult.Ok : new ManifestValidationResult(false, errors);
    }

    /// <summary>
    /// 校验 worker 路径：必须是相对路径、不得包含 <c>..</c>，规范化后仍位于插件目录内。
    /// </summary>
    public static bool IsPathInsidePluginDirectory(string relativePath, string? pluginRootDirectory, out string error)
    {
        error = string.Empty;

        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            error = $"worker 必须是相对路径：\"{relativePath}\"";
            return false;
        }

        var segments = relativePath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            error = $"worker 路径不得包含 \"..\"：\"{relativePath}\"";
            return false;
        }

        if (pluginRootDirectory is null)
        {
            return true;
        }

        var root = Path.GetFullPath(pluginRootDirectory);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            error = $"worker 规范化后跳出插件目录：\"{relativePath}\"";
            return false;
        }

        return true;
    }
}
