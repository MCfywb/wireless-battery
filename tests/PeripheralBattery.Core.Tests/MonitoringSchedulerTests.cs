using PeripheralBattery.PluginSdk;
using Xunit;

namespace PeripheralBattery.Core.Tests;

/// <summary>可编程的假插件客户端，用于验证调度器行为。</summary>
internal sealed class FakePluginClient : IPluginClient
{
    private int _pollCount;
    private int _currentConcurrency;
    private int _maxConcurrency;

    public FakePluginClient(string pluginId, Func<int, PluginPollOutcome>? factory = null)
    {
        PluginId = pluginId;
        Factory = factory;
    }

    public string PluginId { get; }

    public Func<int, PluginPollOutcome>? Factory { get; set; }

    /// <summary>当前并发执行中的 poll 数量峰值。</summary>
    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

    public int PollCount => Volatile.Read(ref _pollCount);

    public Task<PluginPollOutcome> PollAsync(CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _pollCount);
        var current = Interlocked.Increment(ref _currentConcurrency);

        var observed = Volatile.Read(ref _maxConcurrency);
        while (current > observed)
        {
            var previous = Interlocked.CompareExchange(ref _maxConcurrency, current, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }

        try
        {
            var outcome = Factory?.Invoke(index)
                ?? new PluginPollOutcome(true, [], []);
            return Task.FromResult(outcome);
        }
        finally
        {
            Interlocked.Decrement(ref _currentConcurrency);
        }
    }
}

/// <summary>调度器测试。</summary>
public sealed class MonitoringSchedulerTests
{
    private static BatteryReading Reading(int percentage) => new(
        "bt:test",
        "测试设备",
        "test-plugin",
        BatteryComponent.LeftEarbud,
        percentage,
        null,
        true,
        ReadingQuality.Reported,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "TEST");

    [Fact]
    public async Task 启动后应至少完成一次轮询()
    {
        var client = new FakePluginClient("p1");
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        await using var scheduler = new MonitoringScheduler([client], store, TimeSpan.FromSeconds(30));

        scheduler.Start();
        await WaitUntilAsync(() => client.PollCount >= 1);

        Assert.True(client.PollCount >= 1);
    }

    [Fact]
    public async Task 同一插件的轮询不得并行重入()
    {
        var client = new FakePluginClient("p1", _ => new PluginPollOutcome(true, [], []));
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        await using var scheduler = new MonitoringScheduler([client], store, TimeSpan.FromMilliseconds(50));

        scheduler.Start();
        await WaitUntilAsync(() => client.PollCount >= 5);
        await scheduler.StopAsync();

        // §6.4：不允许同一插件的 poll 并行重入。
        Assert.Equal(1, client.MaxConcurrency);
    }

    [Fact]
    public async Task 单个插件失败不得影响其他插件()
    {
        var failing = new FakePluginClient("bad", _ => PluginPollOutcome.Failure("DEVICE_NOT_FOUND", "找不到"));
        var healthy = new FakePluginClient("good", _ => new PluginPollOutcome(true, [Reading(70)], []));

        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        await using var scheduler = new MonitoringScheduler([failing, healthy], store, TimeSpan.FromMilliseconds(50));

        scheduler.Start();
        await WaitUntilAsync(() => healthy.PollCount >= 2);
        await scheduler.StopAsync();

        // 坏插件持续失败，好插件照常工作。
        Assert.True(healthy.PollCount >= 2);
        Assert.Equal(70, store.GetDevice("bt:test")!.Get(BatteryComponent.LeftEarbud)!.Percentage);
    }

    [Fact]
    public async Task 停止后不再继续轮询()
    {
        var client = new FakePluginClient("p1");
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        await using var scheduler = new MonitoringScheduler([client], store, TimeSpan.FromMilliseconds(50));

        scheduler.Start();
        await WaitUntilAsync(() => client.PollCount >= 1);
        await scheduler.StopAsync();

        var afterStop = client.PollCount;
        await Task.Delay(300);

        Assert.Equal(afterStop, client.PollCount);
    }

    [Fact]
    public async Task 立即刷新可触发一次额外轮询()
    {
        var client = new FakePluginClient("p1");
        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        await using var scheduler = new MonitoringScheduler([client], store, TimeSpan.FromHours(1));

        scheduler.Start();
        await WaitUntilAsync(() => client.PollCount >= 1);

        var before = client.PollCount;
        await scheduler.RefreshNowAsync();

        Assert.True(client.PollCount > before);
    }

    [Fact]
    public async Task 插件返回纯错误时必须显示原因而非让数值无声冻结()
    {
        // 回归：插件返回 DEVICE_NOT_FOUND 且读数为空时，宿主曾什么都不记录，
        // UI 一直显示冻结的旧数值却不给出原因，用户无法区分「程序卡住」与「设备没广播」。
        var client = new FakePluginClient("test-plugin", index => index == 1
            ? new PluginPollOutcome(true, [Reading(70)], [])
            : PluginPollOutcome.Failure("DEVICE_NOT_FOUND", "扫描窗口内未收到广播"));

        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        await using var scheduler = new MonitoringScheduler([client], store, TimeSpan.FromMilliseconds(50));

        scheduler.Start();
        await WaitUntilAsync(() => client.PollCount >= 3);
        await scheduler.StopAsync();

        var device = store.GetDevice("bt:test");
        Assert.NotNull(device);
        Assert.Equal(70, device.Get(BatteryComponent.LeftEarbud)!.Percentage);
        Assert.Equal("DEVICE_NOT_FOUND", device.LastErrorCode);
    }

    [Fact]
    public async Task 恢复成功后应清除错误标记()
    {
        var client = new FakePluginClient("test-plugin", index => index switch
        {
            1 => new PluginPollOutcome(true, [Reading(70)], []),
            2 => PluginPollOutcome.Failure("DEVICE_NOT_FOUND", "扫描窗口内未收到广播"),
            _ => new PluginPollOutcome(true, [Reading(55)], []),
        });

        var store = new DeviceStateStore(TimeSpan.FromMinutes(3));
        await using var scheduler = new MonitoringScheduler([client], store, TimeSpan.FromMilliseconds(50));

        scheduler.Start();
        await WaitUntilAsync(() => client.PollCount >= 4);
        await scheduler.StopAsync();

        var device = store.GetDevice("bt:test");
        Assert.NotNull(device);
        Assert.Equal(55, device.Get(BatteryComponent.LeftEarbud)!.Percentage);
        Assert.Null(device.LastErrorCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }
    }
}
