using Windows.Devices.Bluetooth;

namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>系统蓝牙连接状态的一次变化。</summary>
/// <param name="At">变化时刻。</param>
/// <param name="SecondsSinceStart">相对采样开始的秒数。</param>
/// <param name="From">变化前的状态。</param>
/// <param name="To">变化后的状态。</param>
internal sealed record PresenceTransition(
    DateTimeOffset At,
    double SecondsSinceStart,
    string From,
    string To);

/// <summary>
/// 系统蓝牙连接状态采样器。
/// </summary>
/// <remarks>
/// <para>采样 <see cref="BluetoothDevice.ConnectionStatus"/>（经典蓝牙链路状态）。
/// 这是一个本项目此前完全没读过的系统信号：耳机的广播在它与本机保持连接时会被压制，
/// 因此「经典链路是否还连着」是判断耳机是否被踢下线的最直接证据。</para>
/// <para>注意：<c>ConnectionStatus = Connected</c> 只说明经典蓝牙链路存在，
/// <b>不等于</b> A2DP 音频流仍在。音频是否被打断需要另外看音频端点与用户听感。</para>
/// </remarks>
internal sealed class PresenceSampler : IAsyncDisposable
{
    private readonly BluetoothDevice _device;
    private readonly TimeSpan _interval;
    private readonly List<PresenceTransition> _transitions = [];
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _last = "?";

    public PresenceSampler(BluetoothDevice device, TimeSpan interval)
    {
        _device = device;
        _interval = interval;
    }

    /// <summary>采样期间记录到的状态变化。</summary>
    public IReadOnlyList<PresenceTransition> Transitions
    {
        get
        {
            lock (_gate)
            {
                return _transitions.ToList();
            }
        }
    }

    /// <summary>最近一次读到的状态。</summary>
    public string Current => _last;

    /// <summary>开始后台采样。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        _last = SafeRead();

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var current = SafeRead();
            if (!string.Equals(current, _last, StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _transitions.Add(new PresenceTransition(
                        DateTimeOffset.UtcNow,
                        Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 2),
                        _last,
                        current));
                }

                _last = current;
            }
        }
    }

    private string SafeRead()
    {
        try
        {
            return _device.ConnectionStatus.ToString();
        }
        catch (Exception ex)
        {
            return $"读取失败({ex.GetType().Name})";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常结束。
            }

            _loop = null;
        }
    }
}
