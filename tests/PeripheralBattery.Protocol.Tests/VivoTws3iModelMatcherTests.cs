using PeripheralBattery.PluginSdk;
using VivoTws3i.Protocol;
using Xunit;

namespace PeripheralBattery.Protocol.Tests;

/// <summary>型号匹配器测试：身份判定必须以 model id 为依据。</summary>
public class VivoTws3iModelMatcherTests
{
    private static VivoTws3iModelMatcher CreateMatcher()
    {
        return new VivoTws3iModelMatcher(new VivoTws3iProfile());
    }

    [Theory]
    [InlineData(132)]
    [InlineData(133)]
    public void 支持列表内的型号_判定为Supported(int modelId)
    {
        var matcher = CreateMatcher();

        Assert.Equal(ModelMatchVerdict.Supported, matcher.Evaluate(modelId));
        Assert.True(matcher.IsSupported(modelId));
    }

    [Theory]
    [InlineData(144)]
    [InlineData(145)]
    public void 同系列但未验证的型号_判定为KnownButUnverified(int modelId)
    {
        var matcher = CreateMatcher();

        Assert.Equal(ModelMatchVerdict.KnownButUnverified, matcher.Evaluate(modelId));
        Assert.False(matcher.IsSupported(modelId));
    }

    [Theory]
    [InlineData(112)]
    [InlineData(124)]
    [InlineData(256)]
    public void 其它型号_判定为Unknown(int modelId)
    {
        var matcher = CreateMatcher();

        Assert.Equal(ModelMatchVerdict.Unknown, matcher.Evaluate(modelId));
        Assert.False(matcher.IsSupported(modelId));
    }

    [Fact]
    public void 未知型号_展示名回退为通用描述()
    {
        var matcher = CreateMatcher();

        Assert.Equal("vivo TWS 3i", matcher.GetDisplayName(132));
        Assert.Equal("vivo TWS (model 999)", matcher.GetDisplayName(999));
    }
}

/// <summary>manifest 校验测试：规则取自 docs/development-plan.md §5.2。</summary>
public class PluginManifestValidatorTests
{
    private static PluginManifest ValidManifest() => new()
    {
        Id = "vivo-tws-3i",
        Name = "vivo TWS 3i 电量适配器",
        Version = "0.1.0",
        MinHostVersion = "0.1.0",
        Worker = "worker/VivoTws3i.Worker.exe",
        Transports = ["ble-advertisement"],
        Capabilities = new PluginCapabilities { BatteryComponents = ["leftEarbud", "rightEarbud", "chargingCase"] },
        Verification = new VerificationInfo { Status = VerificationStatuses.Captured },
    };

    [Fact]
    public void 合法manifest_校验通过()
    {
        var result = PluginManifestValidator.Validate(ValidManifest());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("VivoTws3i")]
    [InlineData("vivo_tws_3i")]
    [InlineData("vivo tws 3i")]
    [InlineData("../escape")]
    [InlineData("vivo--tws")]
    [InlineData("")]
    public void 非法id_校验失败(string id)
    {
        var manifest = ValidManifest();
        manifest.Id = id;

        var result = PluginManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../outside/worker.exe")]
    [InlineData("worker/../../worker.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("/usr/bin/worker")]
    public void worker路径跳出插件目录_校验失败(string worker)
    {
        var manifest = ValidManifest();
        manifest.Worker = worker;

        var result = PluginManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("worker", StringComparison.Ordinal));
    }

    [Fact]
    public void worker规范化后仍在插件目录内_校验通过()
    {
        var manifest = ValidManifest();
        manifest.Worker = "worker/./VivoTws3i.Worker.exe";

        var result = PluginManifestValidator.Validate(manifest, Path.GetTempPath());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void 缺失必需字段_校验失败()
    {
        var manifest = ValidManifest();
        manifest.Name = string.Empty;
        manifest.Transports.Clear();
        manifest.Capabilities.BatteryComponents.Clear();

        var result = PluginManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("name", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("transports", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("batteryComponents", StringComparison.Ordinal));
    }

    [Fact]
    public void 未知manifest版本_校验失败()
    {
        var manifest = ValidManifest();
        manifest.ManifestVersion = 99;

        var result = PluginManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("manifestVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void 非法验证状态_校验失败()
    {
        var manifest = ValidManifest();
        manifest.Verification.Status = "verified";

        var result = PluginManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("verification.status", StringComparison.Ordinal));
    }

    [Fact]
    public void 实际插件manifest_校验通过()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "manifest.json");
        Assert.True(File.Exists(manifestPath), $"未找到 manifest：{manifestPath}");

        var manifest = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        var result = PluginManifestValidator.Validate(manifest!, Path.GetDirectoryName(manifestPath));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void 实际插件profile_型号列表与manifest一致()
    {
        var profilePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "profile.json");
        Assert.True(File.Exists(profilePath), $"未找到 profile：{profilePath}");

        var profile = System.Text.Json.JsonSerializer.Deserialize<VivoTws3iProfile>(
            File.ReadAllText(profilePath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(profile);

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "manifest.json");
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.Equal(profile!.ModelIds.OrderBy(x => x), manifest!.DeviceMatchers.ModelIds.OrderBy(x => x));
        Assert.Equal("vivo-tws-3i", profile.PluginId);
        Assert.Equal(profile.PluginId, manifest.Id);
    }
}
