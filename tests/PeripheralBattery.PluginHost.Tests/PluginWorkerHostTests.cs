using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.PluginHost.Tests;

/// <summary>
/// 插件运行时测试：熔断、崩溃隔离与健康状态。
/// </summary>
public sealed class PluginWorkerHostTests
{
    private static PluginDescriptor Descriptor(string workerPath) => new()
    {
        Manifest = new PluginManifest
        {
            Id = "test-plugin",
            Name = "测试插件",
            Version = "0.1.0",
            MinHostVersion = "0.1.0",
            Worker = "worker.exe",
            Transports = ["ble-advertisement"],
            Capabilities = new PluginCapabilities { BatteryComponents = ["leftEarbud"] },
            Verification = new VerificationInfo { Status = VerificationStatuses.HardwareVerified },
        },
        PluginRootDirectory = Path.GetDirectoryName(workerPath)!,
        WorkerExecutablePath = workerPath,
    };

    [Fact]
    public async Task Worker不可用时连续失败达到阈值应熔断()
    {
        // 指向不存在的可执行文件：每次轮询都会启动失败。
        var missing = Path.Combine(Path.GetTempPath(), "peripheral-battery-not-exist", "worker.exe");
        var options = new PluginHostOptions { FailureThreshold = 3, RestartBackoffMax = TimeSpan.Zero };

        await using var host = new PluginWorkerHost(Descriptor(missing), options);

        var first = await host.PollAsync(CancellationToken.None);
        var second = await host.PollAsync(CancellationToken.None);
        var third = await host.PollAsync(CancellationToken.None);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.False(third.Ok);

        Assert.Equal(3, host.ConsecutiveFailures);
        Assert.Equal(PluginHealthState.CircuitOpen, host.Health);
    }

    [Fact]
    public async Task 熔断后应拒绝继续轮询并可手动重启()
    {
        var missing = Path.Combine(Path.GetTempPath(), "peripheral-battery-not-exist", "worker.exe");
        var options = new PluginHostOptions { FailureThreshold = 1, RestartBackoffMax = TimeSpan.Zero };

        await using var host = new PluginWorkerHost(Descriptor(missing), options);

        await host.PollAsync(CancellationToken.None);
        Assert.Equal(PluginHealthState.CircuitOpen, host.Health);

        var afterCircuit = await host.PollAsync(CancellationToken.None);
        Assert.False(afterCircuit.Ok);
        Assert.Equal("PLUGIN_CIRCUIT_OPEN", afterCircuit.ErrorCode);

        // 用户重启后熔断状态解除（可执行文件仍不存在，故仍会失败，但失败计数已重置）。
        await host.RestartAsync(CancellationToken.None);
        Assert.Equal(0, host.ConsecutiveFailures);
        Assert.NotEqual(PluginHealthState.CircuitOpen, host.Health);
    }

    [Fact]
    public async Task 单个插件失败不得抛出异常终止宿主()
    {
        var missing = Path.Combine(Path.GetTempPath(), "peripheral-battery-not-exist", "worker.exe");
        var options = new PluginHostOptions { FailureThreshold = 10, RestartBackoffMax = TimeSpan.Zero };

        await using var host = new PluginWorkerHost(Descriptor(missing), options);

        // 不应抛出：失败以返回值表达。
        var outcome = await host.PollAsync(CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.ErrorCode);
    }

    [Fact]
    public async Task 停止后健康状态应为Stopped()
    {
        var missing = Path.Combine(Path.GetTempPath(), "peripheral-battery-not-exist", "worker.exe");

        await using var host = new PluginWorkerHost(Descriptor(missing), new PluginHostOptions());
        await host.StopAsync();

        Assert.Equal(PluginHealthState.Stopped, host.Health);
    }
}
