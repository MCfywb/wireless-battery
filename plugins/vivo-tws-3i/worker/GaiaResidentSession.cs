using System.Diagnostics;
using VivoTws3i.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace VivoTws3i.Worker;

/// <summary>常驻会话从电量帧解码出的三部件电量。</summary>
/// <param name="ReceivedAtUtc">收到该帧的时刻。</param>
/// <param name="DeviceId">设备标识（与被动广播同一格式）。</param>
/// <param name="Left">左耳电量；<c>null</c> = 未知（协议报 0xFF）。</param>
/// <param name="Right">右耳电量；<c>null</c> = 未知。</param>
/// <param name="Case">充电盒电量；<c>null</c> = 未知。</param>
/// <param name="CaseCharging">盒是否在充电（bit2，已验证）；未知时 <c>null</c>。</param>
internal sealed record ResidentBattery(
    DateTimeOffset ReceivedAtUtc,
    string DeviceId,
    int? Left,
    int? Right,
    int? Case,
    bool? CaseCharging);

/// <summary>常驻会话的连接状态。</summary>
internal enum ResidentSessionState
{
    /// <summary>未连接（初始 / 已断开）。</summary>
    Disconnected,

    /// <summary>正在建链（SDP / 连接 / 握手 / 注册链）。</summary>
    Connecting,

    /// <summary>已连接，读循环在跑。</summary>
    Connected,
}

/// <summary>
/// 常驻 RFCOMM（GAIA）会话：启动即建链并保持，持续接收设备自发推送。
/// </summary>
/// <remarks>
/// <para><b>与 <see cref="GaiaActiveReader"/> 的区别</b>：后者是「连上 → 取一帧 → 断开」
/// （用户点刷新时走），本类是「连上 → <b>保持</b> → 持续收帧 → 断了再连」。</para>
/// <para><b>建链代码复用</b>：设备查找、SDP、socket 连接、发送、帧读取全部复用
/// <see cref="GaiaActiveReader"/> 中<b>已经真机验证过</b>的实现，不另写一套，
/// 避免两套实现各自演化出不同的 bug。</para>
/// <para><b>三条实测硬约束（协议笔记 §23.5）</b>：</para>
/// <list type="number">
/// <item><description>同一设备的同一 RFCOMM 通道同时只允许一个 socket——
/// 重连前<b>必须先释放旧会话</b>，否则被系统拒绝（地址已占用）。</description></item>
/// <item><description>断连以「干净 EOF」结束（读到 0 字节），不是异常也不是挂起，
/// 因此读循环可据此判定断连，无需超时兜底。</description></item>
/// <item><description>刚上电时 SDP 明显变慢（实测 2653 ms，是首次建链 294 ms 的约 7.6 倍），
/// 重连的超时预算不能照搬首次建链的经验。</description></item>
/// </list>
/// <para><b>重连触发</b>：由调用方传入的在线判定决定，<b>不做盲目重试</b>。
/// 实测（§23.3）系统蓝牙状态比 socket 早约 5 秒感知断电，
/// 且断电过程中会瞬时回连一次，故在线判定本身必须去抖。</para>
/// </remarks>
internal sealed class GaiaResidentSession : IDisposable
{
    /// <summary>重连退避的初值与上限。</summary>
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 读循环每次等待的时长。
    /// </summary>
    /// <remarks>与 <see cref="GaiaActiveReader.SocketReader"/> 的语义一致：
    /// 超时只返回 null 并<b>保留挂起读</b>，不重新发起 LoadAsync（WinRT 不允许多个挂起读）。</remarks>
    private const int ReadChunkMs = 250;

    private readonly string _deviceName;
    private readonly Func<bool?> _presenceProvider;

    private readonly object _gate = new();
    private readonly List<string> _diagnostics = [];

    /// <summary>发送应答时的诊断收集器（常驻会话不对外暴露这些细节，故单独收集后丢弃）。</summary>
    private readonly List<string> _replyLog = [];

    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorTask;

    private ResidentSessionState _state = ResidentSessionState.Disconnected;
    private StreamSocket? _socket;
    private DataWriter? _writer;
    private GaiaActiveReader.SocketReader? _reader;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;

    private ResidentBattery? _latest;
    private DateTimeOffset _connectedAtUtc;
    private string _lastError = string.Empty;
    private DateTimeOffset _nextAttemptAtUtc;
    private TimeSpan _backoff = InitialBackoff;

    /// <summary>构造常驻会话。</summary>
    /// <param name="deviceName">经典蓝牙设备名（无 MAC 时的兜底）。</param>
    /// <param name="presenceProvider">
    /// 在线判定（去抖后）：<c>true</c> = 在线，<c>false</c> = 离线，<c>null</c> = 未判定。
    /// 只在返回 <c>true</c> 时才会尝试建链/重连——避免设备不在时盲目重试。
    /// </param>
    public GaiaResidentSession(string deviceName, Func<bool?> presenceProvider)
    {
        _deviceName = deviceName;
        _presenceProvider = presenceProvider;
    }

    /// <summary>当前状态。</summary>
    public ResidentSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>最近一次收到的电量帧；从未收到则为 <c>null</c>。</summary>
    public ResidentBattery? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>最近一次失败原因（诊断用）。</summary>
    public string LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>取出并清空累积的诊断文本。</summary>
    public IReadOnlyList<string> DrainDiagnostics()
    {
        lock (_gate)
        {
            var copy = _diagnostics.ToArray();
            _diagnostics.Clear();
            return copy;
        }
    }

    /// <summary>启动监督循环（后台）。</summary>
    public void Start()
    {
        _supervisorCts = new CancellationTokenSource();
        var token = _supervisorCts.Token;
        _supervisorTask = Task.Run(() => SuperviseAsync(token), CancellationToken.None);
    }

    private async Task SuperviseAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool shouldAttempt;
            lock (_gate)
            {
                shouldAttempt = _state == ResidentSessionState.Disconnected
                    && DateTimeOffset.UtcNow >= _nextAttemptAtUtc;
            }

            if (!shouldAttempt)
            {
                continue;
            }

            // 只在线判为 true 时才建链：设备不在就别白费力气，
            // 实测设备离线时设备枚举可能挂起数十秒。
            if (_presenceProvider() != true)
            {
                continue;
            }

            await TryConnectAsync(token).ConfigureAwait(false);
        }
    }

    private async Task TryConnectAsync(CancellationToken token)
    {
        // 建链前必须先释放旧 socket：同一 RFCOMM 通道只允许一个，
        // 否则新建连接会被系统以「地址已占用」拒绝。
        ReleaseSession();

        lock (_gate)
        {
            _state = ResidentSessionState.Connecting;
        }

        var log = new List<string>();
        string? mac;
        lock (_gate)
        {
            mac = _knownMac;
        }

        var device = await GaiaActiveReader
            .FindDeviceAsync(_deviceName, mac, log)
            .ConfigureAwait(false);

        if (device is null)
        {
            Fail("未找到经典蓝牙设备");
            return;
        }

        var socket = await GaiaActiveReader
            .ConnectAsync(device, log)
            .ConfigureAwait(false);

        if (socket is null)
        {
            Fail("无法建立私有 RFCOMM 通道");
            return;
        }

        var writer = new DataWriter(socket.OutputStream);
        var reader = new GaiaActiveReader.SocketReader(socket.InputStream);

        var deviceId = VivoTwsDeviceId.FromAddress(socket.Information.RemoteAddress.CanonicalName);

        // 握手
        var handshake = VivoTwsGaiaFrameReader.Build(
            GaiaActiveReader.HandshakeVersion,
            VivoTwsGaiaFrameReader.VendorGaia,
            VivoTwsGaiaFrameReader.CommandHandshake,
            []);

        await GaiaActiveReader.SendAsync(writer, handshake, log).ConfigureAwait(false);

        var handshakeBytes = await reader.CollectAsync(GaiaActiveReader.HandshakeResponseMs, token)
            .ConfigureAwait(false);

        var handshakeFrames = VivoTwsGaiaFrameReader.Parse(handshakeBytes);
        var ack = handshakeFrames.FirstOrDefault(
            f => f.Command == VivoTwsGaiaFrameReader.CommandHandshakeAck);

        if (ack is null)
        {
            reader.Dispose();
            writer.DetachStream();
            writer.Dispose();
            socket.Dispose();
            Fail("GAIA 握手未收到响应");
            return;
        }

        // 注册通知链：注册完成后设备会在状态变化时上报电量。
        for (var cmd = 0x0202; cmd <= 0x0206; cmd++)
        {
            var frame = VivoTwsGaiaFrameReader.Build(
                GaiaActiveReader.HandshakeVersion,
                VivoTwsGaiaFrameReader.VendorVivo,
                cmd,
                []);

            await GaiaActiveReader.SendAsync(writer, frame, log).ConfigureAwait(false);
            await Task.Delay(GaiaActiveReader.RegisterGapMs, token).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _socket = socket;
            _writer = writer;
            _reader = reader;
            _state = ResidentSessionState.Connected;
            _connectedAtUtc = DateTimeOffset.UtcNow;
            _backoff = InitialBackoff;
            _lastError = string.Empty;
            _diagnostics.Add($"resident:connected deviceId={deviceId}");
        }

        // 启动读循环：持续接收推送，并在对端关闭时判定断连。
        _readCts = new CancellationTokenSource();
        var readToken = _readCts.Token;
        _readTask = Task.Run(() => ReadLoopAsync(reader, writer, deviceId, readToken), CancellationToken.None);
    }

    private async Task ReadLoopAsync(
        GaiaActiveReader.SocketReader reader,
        DataWriter writer,
        string deviceId,
        CancellationToken token)
    {
        var buffer = new List<byte>();

        while (!token.IsCancellationRequested)
        {
            byte[]? chunk;
            try
            {
                chunk = await reader.ReadAsync(ReadChunkMs, token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                // 干净的对端关闭：实测断连就是这个形式（读到 0 字节）。
                MarkDisconnected("eof：对端关闭连接（读到 0 字节）");
                return;
            }
            catch (Exception ex)
            {
                MarkDisconnected($"{ex.GetType().Name}: {ex.Message} (HResult 0x{ex.HResult:X8})");
                return;
            }

            if (chunk is null)
            {
                continue;
            }

            buffer.AddRange(chunk);

            var frames = new List<VivoTwsGaiaFrame>();
            var consumed = VivoTwsGaiaFrameReader.ParseAndConsume(buffer.ToArray(), frames);
            if (consumed > 0)
            {
                buffer.RemoveRange(0, consumed);
            }

            foreach (var frame in frames)
            {
                if (frame.Vendor == VivoTwsGaiaFrameReader.VendorVivo
                    && frame.Command == VivoTwsGaiaFrameReader.CommandTimeRequest)
                {
                    // 上游 probe 注明「必须应答，否则它可能不继续交互」。
                    // 实测两次常驻测试中该命令均未出现，属防御性实现。
                    var timeReply = VivoTwsGaiaFrameReader.Build(
                        3,
                        VivoTwsGaiaFrameReader.VendorVivo,
                        VivoTwsGaiaFrameReader.CommandTimeResponse,
                        BuildTimePayload());

                    await GaiaActiveReader.SendAsync(writer, timeReply, _replyLog).ConfigureAwait(false);
                    continue;
                }

                if (frame.Command != VivoTwsGaiaFrameReader.CommandBatteryReport)
                {
                    continue;
                }

                // 与主动读取路径共用同一个解码器，保证两条路径语义一致。
                var decoded = VivoTwsGaiaFrameReader.DecodeBattery(frame.Payload);
                if (decoded is null)
                {
                    continue;
                }

                lock (_gate)
                {
                    _latest = new ResidentBattery(
                        DateTimeOffset.UtcNow,
                        deviceId,
                        decoded.Left,
                        decoded.Right,
                        decoded.Case,
                        decoded.CaseCharging);

                    _diagnostics.Add(
                        $"resident:battery 左={decoded.Left?.ToString() ?? "未知"}"
                        + $" 右={decoded.Right?.ToString() ?? "未知"}"
                        + $" 盒={decoded.Case?.ToString() ?? "未知"}");
                }
            }
        }
    }

    private void MarkDisconnected(string reason)
    {
        lock (_gate)
        {
            var held = Math.Round((DateTimeOffset.UtcNow - _connectedAtUtc).TotalSeconds, 1);
            _state = ResidentSessionState.Disconnected;
            _lastError = reason;
            _diagnostics.Add($"resident:disconnected afterSec={held} reason={reason}");

            // 退避递增，避免设备不在时反复重连。
            _backoff = _backoff >= MaxBackoff ? MaxBackoff : TimeSpan.FromTicks(_backoff.Ticks * 2);
            _nextAttemptAtUtc = DateTimeOffset.UtcNow + _backoff;
        }

        ReleaseSession();
    }

    private void Fail(string reason)
    {
        lock (_gate)
        {
            _state = ResidentSessionState.Disconnected;
            _lastError = reason;
            _diagnostics.Add($"resident:connectFailed reason={reason}");
            _backoff = _backoff >= MaxBackoff ? MaxBackoff : TimeSpan.FromTicks(_backoff.Ticks * 2);
            _nextAttemptAtUtc = DateTimeOffset.UtcNow + _backoff;
        }
    }

    /// <summary>
    /// 释放当前 socket 与读写器。
    /// </summary>
    /// <remarks>
    /// 必须先于任何新建连接调用：Windows 上一台设备的同一 RFCOMM 通道同时只允许一个 socket，
    /// 旧的不放，新的必然被拒（实测 COMException「地址已占用」）。
    /// </remarks>
    private void ReleaseSession()
    {
        if (_readCts is not null)
        {
            _readCts.Cancel();
            _readCts.Dispose();
            _readCts = null;
        }

        if (_readTask is not null)
        {
            try
            {
                _readTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 读循环被取消属正常退出。
            }

            _readTask = null;
        }

        _reader?.Dispose();
        _reader = null;

        if (_writer is not null)
        {
            try
            {
                _writer.DetachStream();
                _writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // 关闭异常可忽略。
            }

            _writer = null;
        }

        _socket?.Dispose();
        _socket = null;
    }

    /// <summary>已知 MAC（被动广播探明后设置，用于按地址精确匹配）。</summary>
    private string? _knownMac;

    /// <summary>设置已知 MAC。</summary>
    public void SetKnownMac(string? mac)
    {
        lock (_gate)
        {
            _knownMac = mac;
        }
    }

    /// <summary>时间应答载荷：7 字节，完整年份 = 世纪 * 100 + 年。</summary>
    private static byte[] BuildTimePayload()
    {
        var now = DateTime.Now;
        return
        [
            (byte)((now.Year / 100) & 0xFF),
            (byte)(now.Year % 100),
            (byte)now.Month,
            (byte)now.Day,
            (byte)now.Hour,
            (byte)now.Minute,
            (byte)now.Second,
        ];
    }

    public void Dispose()
    {
        if (_supervisorCts is not null)
        {
            _supervisorCts.Cancel();
            _supervisorCts.Dispose();
            _supervisorCts = null;
        }

        if (_supervisorTask is not null)
        {
            try
            {
                _supervisorTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 监督任务被取消属正常退出。
            }

            _supervisorTask = null;
        }

        ReleaseSession();
    }
}
