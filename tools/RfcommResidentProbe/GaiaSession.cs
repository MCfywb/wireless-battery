using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>帧类别。</summary>
internal enum GaiaFrameKind
{
    /// <summary>电量帧 <c>0x8207</c>。</summary>
    Battery,

    /// <summary>佩戴状态帧 <c>0x820D</c>。</summary>
    Wear,

    /// <summary>耳机索要时间的请求帧 <c>0x8509</c>。</summary>
    TimeRequest,

    /// <summary>握手应答 <c>0x8300</c>。</summary>
    HandshakeAck,

    /// <summary>遥测帧 <c>0x8224</c>：载荷是 JSON，含耳机自报的音频链路状态。</summary>
    Telemetry,

    /// <summary>其它帧。</summary>
    Other,
}

/// <summary>收到的一帧事件。</summary>
internal sealed record GaiaFrameEvent(
    DateTimeOffset At,
    double SecondsSinceConnect,
    GaiaFrameKind Kind,
    int Command,
    int Vendor,
    byte[] Payload,
    string Summary);

/// <summary>连接建立的各阶段耗时。</summary>
internal sealed record ConnectTiming(
    int SdpMs,
    int ConnectMs,
    int HandshakeMs,
    int RegisterMs,
    int TotalMs,
    int SdpHits,
    string? GaiaVersion,
    string DeviceId);

/// <summary>断连信息：常驻连接实验最关心的数据。</summary>
internal sealed record DisconnectInfo(
    DateTimeOffset At,
    double SecondsSinceConnect,
    string Reason,
    string? ExceptionType,
    string? HResult,
    string? Message);

/// <summary>
/// 常驻 RFCOMM（GAIA）会话。
/// </summary>
/// <remarks>
/// <para><b>与产品代码的差异</b>：产品侧 <c>GaiaActiveReader</c> 是「连上 → 取一帧 → 断开」，
/// 本类是「连上 → <b>保持</b> → 持续收帧 → 直到对端断开」，用于验证常驻连接是否可行。</para>
/// <para><b>必须应答 0x8509</b>：耳机在会话中会主动索要时间（命令 <c>0x8509</c>），
/// 上游 probe 注明「必须应答，否则它可能不继续交互」。产品代码目前<b>没有</b>实现该应答，
/// 这对「按需连、用完断」无影响，但常驻连接必须补上——否则耳机可能在若干秒后停止推送。
/// 本探针实现了应答，并在报告中单独计数，用于观察耳机的索要频率。</para>
/// </remarks>
internal sealed class GaiaSession : IDisposable
{
    /// <summary>vivo 私有 RFCOMM 服务 UUID。</summary>
    private const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

    /// <summary>握手版本：本机实测必须用 v4（v3 无响应）。</summary>
    private const int HandshakeVersion = 4;

    private const ushort VendorGaia = 0x000A;
    private const ushort VendorVivo = 0x001B;
    private const ushort CommandHandshake = 0x0300;
    private const ushort CommandHandshakeAck = 0x8300;
    private const ushort CommandBatteryReport = 0x8207;
    private const ushort CommandWearState = 0x820D;
    private const ushort CommandTimeRequest = 0x8509;
    private const ushort CommandTimeResponse = 0x0509;

    /// <summary>遥测帧命令字。</summary>
    private const ushort CommandTelemetry = 0x8224;

    private const int SdpTimeoutMs = 20000;
    private const int ConnectTimeoutMs = 15000;
    private const int HandshakeTimeoutMs = 3000;

    /// <summary>设备枚举超时（对齐产品代码 <c>GaiaActiveReader.FindTimeoutMs</c>）。</summary>
    private const int FindTimeoutMs = 10000;

    /// <summary>
    /// 按地址枚举的专用超时（对齐产品代码 <c>GaiaActiveReader.AddressFindTimeoutMs</c>）。
    /// 设备处于连接态时该查询会挂起，4 秒即放弃转名字兜底。
    /// </summary>
    private const int AddressFindTimeoutMs = 4000;

    private const int RegisterGapMs = 1200;
    private const int SendTimeoutMs = 5000;

    private readonly DataWriter _writer;
    private readonly GaiaSocketReader _reader;
    private readonly Stopwatch _sinceConnect = new();

    private StreamSocket? _socket;
    private bool _disposed;

    private GaiaSession(StreamSocket socket)
    {
        _socket = socket;
        _writer = new DataWriter(socket.OutputStream);
        _reader = new GaiaSocketReader(socket.InputStream);
    }

    /// <summary>连接是否仍处于活动状态。</summary>
    public bool IsConnected => _socket is not null && !_disposed;

    /// <summary>
    /// 定位并建立常驻会话。
    /// </summary>
    /// <param name="deviceName">经典蓝牙设备名（模糊匹配用）。</param>
    /// <param name="knownMac">已知 MAC（<c>XX:XX:…</c>）；有值时优先按地址精确匹配。</param>
    /// <param name="log">过程日志回调。</param>
    public static async Task<(GaiaSession? Session, ConnectTiming? Timing, string? Error)> ConnectAsync(
        string deviceName,
        string? knownMac,
        Action<string> log)
    {
        var device = await FindDeviceAsync(deviceName, knownMac, log).ConfigureAwait(false);
        if (device is null)
        {
            return (null, null, $"未找到经典蓝牙设备「{deviceName}」");
        }

        log($"经典蓝牙 ConnectionStatus = {device.ConnectionStatus}");

        var total = Stopwatch.StartNew();

        // 1) 定向 SDP
        var sdpWatch = Stopwatch.StartNew();
        var serviceId = RfcommServiceId.FromUuid(Guid.Parse(VivoPrivateUuid));
        var sdpTask = device.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached).AsTask();
        if (await Task.WhenAny(sdpTask, Task.Delay(SdpTimeoutMs)).ConfigureAwait(false) != sdpTask)
        {
            return (null, null, $"SDP 查询超时（{SdpTimeoutMs} ms，设备可能未连接）");
        }

        var sdp = await sdpTask.ConfigureAwait(false);
        var sdpMs = (int)sdpWatch.ElapsedMilliseconds;
        log($"SDP 命中 {sdp.Services.Count} 个服务，耗时 {sdpMs} ms（error={sdp.Error}）");
        if (sdp.Services.Count == 0)
        {
            return (null, null, "SDP 未命中私有通道，无法建立常驻连接");
        }

        var service = sdp.Services[0];

        // 2) 建立 RFCOMM 连接
        var connectWatch = Stopwatch.StartNew();
        var socket = new StreamSocket();
        try
        {
            var connectTask = socket.ConnectAsync(
                service.ConnectionHostName,
                service.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask();

            if (await Task.WhenAny(connectTask, Task.Delay(ConnectTimeoutMs)).ConfigureAwait(false) != connectTask)
            {
                socket.Dispose();
                return (null, null, $"RFCOMM 连接超时（{ConnectTimeoutMs} ms）");
            }

            await connectTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            return (null, null, $"RFCOMM 连接失败：{ex.GetType().Name}: {ex.Message} (HResult 0x{ex.HResult:X8})");
        }

        var connectMs = (int)connectWatch.ElapsedMilliseconds;
        log($"RFCOMM 连接成功，耗时 {connectMs} ms");

        var session = new GaiaSession(socket);
        session._sinceConnect.Start();

        var deviceId = socket.Information.RemoteAddress.CanonicalName ?? "unknown";

        // 3) 握手
        var handshakeWatch = Stopwatch.StartNew();
        var handshake = GaiaFrameCodec.Build(HandshakeVersion, VendorGaia, CommandHandshake, []);
        await session.SendAsync(handshake, log).ConfigureAwait(false);

        var handshakeBytes = await session.ReadForAsync(HandshakeTimeoutMs, log).ConfigureAwait(false);
        var handshakeMs = (int)handshakeWatch.ElapsedMilliseconds;
        string? gaiaVersion = null;

        if (handshakeBytes is not null)
        {
            var (_, frames) = GaiaFrameCodec.ParseAndConsume(handshakeBytes.ToList());
            foreach (var frame in frames)
            {
                log($"  握手响应 cmd=0x{frame.Command:X4} ver={frame.Version} payload={Convert.ToHexString(frame.Payload)}");
                if (frame.Command == CommandHandshakeAck && frame.Payload.Length >= 4)
                {
                    gaiaVersion = $"{frame.Payload[1]}.{frame.Payload[2]}.{frame.Payload[3]}";
                }
            }
        }

        if (gaiaVersion is null)
        {
            session.Dispose();
            return (null, null, $"GAIA 握手未收到 0x8300 应答（耗时 {handshakeMs} ms）");
        }

        log($"GAIA 握手成功，设备报告版本 {gaiaVersion}，耗时 {handshakeMs} ms");

        // 4) 注册通知链
        var registerWatch = Stopwatch.StartNew();
        for (var cmd = 0x0202; cmd <= 0x0206; cmd++)
        {
            var frame = GaiaFrameCodec.Build(HandshakeVersion, VendorVivo, cmd, []);
            await session.SendAsync(frame, log).ConfigureAwait(false);
            await Task.Delay(RegisterGapMs).ConfigureAwait(false);
        }

        var registerMs = (int)registerWatch.ElapsedMilliseconds;
        log($"注册链 0x0202~0x0206 完成，耗时 {registerMs} ms");

        var timing = new ConnectTiming(
            sdpMs, connectMs, handshakeMs, registerMs, (int)total.ElapsedMilliseconds,
            sdp.Services.Count, gaiaVersion, deviceId);

        return (session, timing, null);
    }

    /// <summary>
    /// 持续读取帧，直到对端断开或被取消。
    /// </summary>
    /// <param name="onFrame">帧回调。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>断连信息；若是被取消则返回 <c>null</c>。</returns>
    public async Task<DisconnectInfo?> ReadLoopAsync(Action<GaiaFrameEvent> onFrame, CancellationToken token)
    {
        var buffer = new List<byte>();

        while (!token.IsCancellationRequested)
        {
            byte[]? chunk;
            try
            {
                chunk = await _reader.ReadAsync(250).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                return BuildDisconnect("eof", ex);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                return BuildDisconnect("error", ex);
            }

            if (chunk is null)
            {
                continue;
            }

            buffer.AddRange(chunk);
            var (consumed, frames) = GaiaFrameCodec.ParseAndConsume(buffer);
            if (consumed > 0)
            {
                buffer.RemoveRange(0, consumed);
            }

            foreach (var frame in frames)
            {
                var kind = Classify(frame);
                var at = DateTimeOffset.UtcNow;

                if (kind == GaiaFrameKind.TimeRequest)
                {
                    // 必须应答，否则耳机可能不再继续交互（上游 probe 要求）。
                    await SendAsync(
                        GaiaFrameCodec.Build(3, VendorVivo, CommandTimeResponse, BuildTimePayload()),
                        _ => { }).ConfigureAwait(false);
                }

                onFrame(new GaiaFrameEvent(
                    at,
                    Math.Round(_sinceConnect.Elapsed.TotalSeconds, 2),
                    kind,
                    frame.Command,
                    frame.Vendor,
                    frame.Payload,
                    Summarize(frame, kind)));
            }
        }

        return null;
    }

    /// <summary>发送一帧。</summary>
    public async Task SendAsync(byte[] payload, Action<string> log)
    {
        try
        {
            _writer.WriteBytes(payload);
            var store = _writer.StoreAsync().AsTask();
            if (await Task.WhenAny(store, Task.Delay(SendTimeoutMs)).ConfigureAwait(false) != store)
            {
                log($"  发送超时（{SendTimeoutMs} ms）");
                return;
            }

            await store.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"  发送失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();

        try
        {
            _writer.DetachStream();
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 忽略关闭异常。
        }

        _socket?.Dispose();
        _socket = null;
    }

    private async Task<byte[]?> ReadForAsync(int timeoutMs, Action<string> log)
    {
        try
        {
            return await _reader.ReadAsync(timeoutMs).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            log($"  握手期间连接已关闭：{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            log($"  握手期间读取失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private DisconnectInfo BuildDisconnect(string reason, Exception ex) => new(
        DateTimeOffset.UtcNow,
        Math.Round(_sinceConnect.Elapsed.TotalSeconds, 2),
        reason,
        ex.GetType().FullName,
        $"0x{ex.HResult:X8}",
        ex.Message);

    private static GaiaFrameKind Classify(GaiaFrame frame)
    {
        if (frame.Vendor == VendorVivo && frame.Command == CommandTimeRequest)
        {
            return GaiaFrameKind.TimeRequest;
        }

        if (frame.Vendor == VendorVivo && frame.Command == CommandBatteryReport)
        {
            return GaiaFrameKind.Battery;
        }

        if (frame.Vendor == VendorVivo && frame.Command == CommandWearState)
        {
            return GaiaFrameKind.Wear;
        }

        if (frame.Vendor == VendorVivo && frame.Command == CommandTelemetry)
        {
            return GaiaFrameKind.Telemetry;
        }

        if (frame.Command == CommandHandshakeAck)
        {
            return GaiaFrameKind.HandshakeAck;
        }

        return GaiaFrameKind.Other;
    }

    private static string Summarize(GaiaFrame frame, GaiaFrameKind kind)
    {
        switch (kind)
        {
            case GaiaFrameKind.Battery when frame.Payload.Length >= 5:
                return $"左={frame.Payload[1]} 右={frame.Payload[2]} 仓={frame.Payload[3]} 充电位=0x{frame.Payload[4]:X2}";
            case GaiaFrameKind.Wear when frame.Payload.Length >= 2:
                return $"佩戴态=0x{frame.Payload[1]:X2}";
            case GaiaFrameKind.TimeRequest:
                return "耳机索要时间（已自动应答）";
            default:
                return $"payload={Convert.ToHexString(frame.Payload)}";
        }
    }

    /// <summary>构造时间应答载荷：7 字节纯二进制，完整年份 = 世纪*100 + 年。</summary>
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

    /// <summary>定位经典蓝牙设备：优先按已知 MAC 精确匹配，回退到按名字模糊枚举。</summary>
    private static async Task<BluetoothDevice?> FindDeviceAsync(
        string deviceName, string? knownMac, Action<string> log)
    {
        if (TryParseMac(knownMac, out var address))
        {
            var byAddress = await WithTimeout(
                DeviceInformation
                    .FindAllAsync(BluetoothDevice.GetDeviceSelectorFromBluetoothAddress(address))
                    .AsTask(),
                AddressFindTimeoutMs, log, "按地址枚举").ConfigureAwait(false);
            log($"按地址枚举（{knownMac}）命中 {byAddress?.Count ?? -1} 个");

            if (byAddress is { Count: > 0 })
            {
                var device = await WithTimeout(
                    BluetoothDevice.FromIdAsync(byAddress[0].Id).AsTask(),
                    FindTimeoutMs, log, "FromIdAsync(地址)").ConfigureAwait(false);
                if (device is not null)
                {
                    log("  → 按地址命中");
                    return device;
                }
            }
        }

        var all = await WithTimeout(
            DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector()).AsTask(),
            FindTimeoutMs, log, "按名称枚举").ConfigureAwait(false);
        log($"按名称枚举共 {all?.Count ?? -1} 个已配对设备");

        if (all is null)
        {
            return null;
        }

        foreach (var info in all)
        {
            if (!info.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var device = await WithTimeout(
                BluetoothDevice.FromIdAsync(info.Id).AsTask(),
                FindTimeoutMs, log, "FromIdAsync(名称)").ConfigureAwait(false);
            if (device is not null)
            {
                log($"  → 按名称命中：{info.Name}");
                return device;
            }
        }

        return null;
    }

    /// <summary>
    /// 给没有内建超时的 WinRT 异步调用套一层超时兜底。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须加</b>：2026-10-27 首次真机测试的教训——
    /// 重连阶段实测耗时 36.8 秒，而建链本身只花 6.8 秒，差的 <b>30 秒是设备枚举在挂起</b>。
    /// <c>DeviceInformation.FindAllAsync</c> 与 <c>BluetoothDevice.FromIdAsync</c> 没有内建超时，
    /// 耳机刚断电/刚上电时尤其容易长时间不返回。产品代码 <c>GaiaActiveReader</c> 每一处都包了
    /// 超时（<c>AddressFindTimeoutMs</c> / <c>FindTimeoutMs</c>），本探针此前漏了，
    /// 导致「首次重连耗时」这个指标被枚举挂起污染、不可用。</para>
    /// <para>超时只观察、不取消底层任务（取消会让底层流被释放，产生二次异常）；
    /// 超时即返回 <c>default</c> 并在日志里留痕。</para>
    /// </remarks>
    private static async Task<T?> WithTimeout<T>(
        Task<T> task, int ms, Action<string> log, string label)
    {
        var delay = Task.Delay(ms);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed != task)
        {
            log($"  {label} 超时（{ms} ms）");
            return default;
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>把 <c>XX:XX:XX:XX:XX:XX</c> 解析为 48 位大端整数。</summary>
    private static bool TryParseMac(string? mac, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(mac))
        {
            return false;
        }

        var parts = mac.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
        {
            return false;
        }

        try
        {
            ulong value = 0;
            foreach (var part in parts)
            {
                value = (value << 8) | Convert.ToByte(part, 16);
            }

            address = value;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
