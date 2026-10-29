using PeripheralBattery.PluginSdk;
using VivoTws3i.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace VivoTws3i.Worker;

/// <summary>
/// 按需主动读取：连私有 RFCOMM 通道 → GAIA 握手 → 注册链 → 短监听取上报 → 断开。
/// </summary>
/// <remarks>
/// <para><b>机理说明（重要）</b>：这不是「发一条查询命令拿回包」。
/// <c>0x0207</c> 电量查询在本型号上 4 种 ver/vendor 组合全部无响应，且官方 App 从不发送它
/// （见 docs/apk-analysis/vivo-tws-app-9.2.40.1.md §12.1/§13.1）。
/// 实测可行路径是：<b>连接 + 注册链之后设备会立即上报一帧 <c>0x8207</c></b>，
/// 期间的状态变化还会追加推送（docs/protocols/vivo-tws-3i.md §21）。</para>
/// <para>按 D-010，本类只在用户显式点击「立即刷新」时被调用；定时轮询不碰它。</para>
/// </remarks>
internal static class GaiaActiveReader
{
    /// <summary>vivo 私有 RFCOMM 服务 UUID。</summary>
    internal const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

    /// <summary>握手用版本：本机实测握手必须用 v4（v3 无响应）。</summary>
    internal const int HandshakeVersion = 4;

    private const int SdpTimeoutMs = 20000;
    private const int ConnectTimeoutMs = 15000;
    private const int FindTimeoutMs = 10000;

    /// <summary>
    /// 按地址枚举的专用超时：命中本地缓存时亚秒级返回；
    /// 设备处于连接态时该查询会挂起（2026-10-27 实测 findByAddress=timeout(10000ms)），
    /// 4 秒即放弃转名字兜底，不再白等 10 秒。
    /// </summary>
    private const int AddressFindTimeoutMs = 4000;

    private const int SendTimeoutMs = 5000;
    internal const int HandshakeResponseMs = 3000;
    // 对齐参考探针 E2：每条注册命令间隔 1.2s，给设备足够处理时间（避免 ACK 漏收）。
    internal const int RegisterGapMs = 1200;

    /// <summary>主动读取结果。</summary>
    /// <param name="Ok">是否取得电量帧。</param>
    /// <param name="DeviceId">设备标识；与被动广播路径同一格式（<c>bt:XX:XX:…</c>），保证宿主能归并。</param>
    /// <param name="Battery">解码后的三部件电量。</param>
    /// <param name="Diagnostics">过程诊断文本。</param>
    /// <param name="ErrorCode">失败码。</param>
    /// <param name="ErrorMessage">失败说明。</param>
    internal sealed record GaiaActiveReadResult(
        bool Ok,
        string? DeviceId,
        VivoTwsGaiaFrameReader.BatteryReport? Battery,
        List<string> Diagnostics,
        string? ErrorCode = null,
        string? ErrorMessage = null);

    /// <summary>执行一次主动读取。</summary>
    /// <param name="deviceName">经典蓝牙设备名（取自 profile 的蓝牙名前缀），仅作兜底匹配用。</param>
    /// <param name="knownMac">被动广播已探明的设备 MAC（冒号分隔大写），与经典蓝牙地址一致，优先用于精确匹配。</param>
    /// <param name="listenMs">建链后等待上报的毫秒数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<GaiaActiveReadResult> ReadAsync(
        string deviceName,
        string? knownMac,
        int listenMs,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();

        var device = await FindDeviceAsync(deviceName, knownMac, diagnostics).ConfigureAwait(false);
        if (device is null)
        {
            return new GaiaActiveReadResult(
                false, null, null, diagnostics,
                PluginErrorCodes.DeviceNotFound,
                $"未找到经典蓝牙设备「{deviceName}」");
        }

        var socket = await ConnectAsync(device, diagnostics).ConfigureAwait(false);
        if (socket is null)
        {
            return new GaiaActiveReadResult(
                false, null, null, diagnostics,
                PluginErrorCodes.DeviceNotConnected,
                "无法建立私有 RFCOMM 通道");
        }

        try
        {
            var writer = new DataWriter(socket.OutputStream);
            using var reader = new SocketReader(socket.InputStream);

            var deviceId = VivoTwsDeviceId.FromAddress(socket.Information.RemoteAddress.CanonicalName);
            diagnostics.Add($"active:deviceId={deviceId}");

            // 1) 握手
            var handshake = VivoTwsGaiaFrameReader.Build(
                HandshakeVersion, VivoTwsGaiaFrameReader.VendorGaia,
                VivoTwsGaiaFrameReader.CommandHandshake, []);
            await SendAsync(writer, handshake, diagnostics).ConfigureAwait(false);

            var handshakeBytes = await reader.CollectAsync(HandshakeResponseMs, cancellationToken)
                .ConfigureAwait(false);
            var handshakeFrames = VivoTwsGaiaFrameReader.Parse(handshakeBytes);
            var ack = handshakeFrames.FirstOrDefault(
                f => f.Command == VivoTwsGaiaFrameReader.CommandHandshakeAck);

            if (ack is null)
            {
                diagnostics.Add($"active:handshake=no-ack frames={handshakeFrames.Count}");
                return new GaiaActiveReadResult(
                    false, deviceId, null, diagnostics,
                    PluginErrorCodes.DeviceNotConnected,
                    "GAIA 握手未收到响应");
            }

            diagnostics.Add($"active:handshake=ok ver={ack.Version}");

            // 2) 注册通知链：注册完成后设备会上报一次状态（含电量）
            for (var cmd = 0x0202; cmd <= 0x0206; cmd++)
            {
                var frame = VivoTwsGaiaFrameReader.Build(
                    HandshakeVersion, VivoTwsGaiaFrameReader.VendorVivo, cmd, []);
                await SendAsync(writer, frame, diagnostics).ConfigureAwait(false);
                await Task.Delay(RegisterGapMs, cancellationToken).ConfigureAwait(false);
            }

            // 3) 短监听：收集所有 0x8207，取最后一帧（最新）
            VivoTwsGaiaFrameReader.BatteryReport? battery = null;
            var reportCount = 0;
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(listenMs);
            var buffer = new List<byte>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var remaining = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
                var chunk = await reader.CollectAsync(
                    Math.Clamp(remaining, 200, 1000), cancellationToken).ConfigureAwait(false);

                if (chunk.Length == 0)
                {
                    continue;
                }

                // 原始字节快照（前 48 字节），用于离线核对帧结构是否如预期。
                var headBytes = chunk.Length <= 48 ? chunk : chunk.Take(48).ToArray();
                diagnostics.Add(
                    $"active:chunk={(chunk.Length > 48 ? "…" : "")}{Convert.ToHexString(headBytes)}");

                buffer.AddRange(chunk);
                var frames = new List<VivoTwsGaiaFrame>();
                var consumed = VivoTwsGaiaFrameReader.ParseAndConsume(buffer.ToArray(), frames);
                if (consumed > 0)
                {
                    // 只移除到最后一个完整帧结束处，半帧留在缓冲等待续传。
                    buffer.RemoveRange(0, consumed);
                }

                foreach (var frame in frames)
                {
                    // 逐帧打印 cmd / vendor / payload 原始字节，便于核对 0x8207 是否被正确解析。
                    diagnostics.Add(
                        $"active:frame cmd=0x{frame.Command:X4} vendor=0x{frame.Vendor:X4} " +
                        $"payload={Convert.ToHexString(frame.Payload)}");

                    if (frame.Command != VivoTwsGaiaFrameReader.CommandBatteryReport)
                    {
                        continue;
                    }

                    reportCount++;

                    var decoded = VivoTwsGaiaFrameReader.DecodeBattery(frame.Payload);

                    // 只保留**可解码**的最后一帧：短帧不可覆盖已解出的值，
                    // 否则会「收到 4 帧却报没有电量」。
                    if (decoded is not null)
                    {
                        battery = decoded;
                    }
                }
            }

            diagnostics.Add($"active:batteryFrames={reportCount}");

            return battery is null
                // 已连入并完成握手，只是设备在窗口内没推送——不能用「未找到设备」掩盖，
                // 否则用户会误以为耳机离线。0x8207 仅在状态变化时上报（§21/§22）。
                ? new GaiaActiveReadResult(
                    false, deviceId, null, diagnostics,
                    PluginErrorCodes.NoFreshReport,
                    $"已连入设备但未收到电量上报（{listenMs / 1000} 秒内耳机状态未变化则不推送）；"
                    + "可取出 / 放回耳机制造一次状态变化后再试")
                : new GaiaActiveReadResult(true, deviceId, battery, diagnostics);
        }
        catch (OperationCanceledException)
        {
            return new GaiaActiveReadResult(
                false, null, null, diagnostics,
                PluginErrorCodes.InternalError, "主动读取被取消");
        }
        catch (Exception ex)
        {
            return new GaiaActiveReadResult(
                false, null, null, diagnostics,
                PluginErrorCodes.InternalError,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            socket.Dispose();
        }
    }

    internal static async Task<BluetoothDevice?> FindDeviceAsync(
        string deviceName, string? knownMac, List<string> diagnostics)
    {
        // 主路径：用被动广播已探明的 MAC 精确匹配。
        // 经验证本型号经典蓝牙地址 == BLE 地址（active:deviceId 与被动 bt: 标识完全一致），
        // 因此 GetDeviceSelectorFromBluetoothAddress 能稳定命中，且不依赖设备名/连接态。
        if (TryParseMac(knownMac, out var addr))
        {
            var byAddr = await WithTimeout(
                DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelectorFromBluetoothAddress(addr)).AsTask(),
                AddressFindTimeoutMs, diagnostics, "findByAddress").ConfigureAwait(false);

            diagnostics.Add($"active:byAddressCandidates={byAddr?.Count ?? -1} mac={knownMac}");
            if (byAddr is { Count: > 0 })
            {
                var dev = await WithTimeout(
                    BluetoothDevice.FromIdAsync(byAddr[0].Id).AsTask(),
                    FindTimeoutMs, diagnostics, "fromId").ConfigureAwait(false);
                if (dev is not null)
                {
                    diagnostics.Add("active:foundBy=address");
                    return dev;
                }
            }
        }

        // 兜底：枚举所有已配对蓝牙设备，按名字（不区分大小写、子串）匹配。
        // 不用 GetDeviceSelectorFromDeviceName（精确匹配且要求设备处于可见/连接态），
        // 配对但未连接的设备它会返回 0，正是此前「classicCandidates=0」的根因。
        var selector = BluetoothDevice.GetDeviceSelector();
        var all = await WithTimeout(
            DeviceInformation.FindAllAsync(selector).AsTask(),
            FindTimeoutMs, diagnostics, "findAll").ConfigureAwait(false);

        diagnostics.Add($"active:enumCount={all?.Count ?? -1}");
        if (all is null || all.Count == 0)
        {
            return null;
        }

        BluetoothDevice? fallback = null;
        foreach (var info in all)
        {
            diagnostics.Add($"active:enumName={info.Name}");
            if (fallback is null
                && info.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                fallback = await WithTimeout(
                    BluetoothDevice.FromIdAsync(info.Id).AsTask(),
                    FindTimeoutMs, diagnostics, "fromId").ConfigureAwait(false);
            }
        }

        if (fallback is not null)
        {
            diagnostics.Add($"active:foundBy=name name={deviceName}");
        }

        return fallback;
    }

    /// <summary>
    /// 把 <c>XX:XX:XX:XX:XX:XX</c> 形式的 MAC 解析为 <see cref="BluetoothDevice.BluetoothAddress"/>
    /// 所用的 48 位大端整数。解析失败返回 <c>false</c>。
    /// </summary>
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

    /// <summary>
    /// 给没有内建超时的 WinRT 异步调用套一层超时兜底。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须加</b>：<c>DeviceInformation.FindAllAsync</c> 与
    /// <c>BluetoothDevice.FromIdAsync</c> 没有内建超时。当设备经历频繁连接/断开或
    /// 上一次 RFCOMM 会话残留半开状态时，WinRT 蓝牙枚举可能长时间不返回，
    /// 进而拖垮整个主动读取直到宿主 90s 超时（实测现象）。</para>
    /// <para>超时只观察、不取消底层任务（取消会让底层流被释放，产生二次异常）；
    /// 超时即返回 <c>default</c> 并在诊断里留痕，由调用方降级处理。</para>
    /// </remarks>
    internal static async Task<T?> WithTimeout<T>(
        Task<T> task, int ms, List<string> diagnostics, string label)
    {
        var delay = Task.Delay(ms);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed != task)
        {
            diagnostics.Add($"active:{label}=timeout({ms}ms)");
            return default;
        }

        return await task.ConfigureAwait(false);
    }

    internal static async Task<StreamSocket?> ConnectAsync(
        BluetoothDevice device, List<string> diagnostics)
    {
        diagnostics.Add($"active:connectionStatus={device.ConnectionStatus}");

        var serviceId = RfcommServiceId.FromUuid(Guid.Parse(VivoPrivateUuid));
        var sdp = device.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached).AsTask();
        if (await Task.WhenAny(sdp, Task.Delay(SdpTimeoutMs)).ConfigureAwait(false) != sdp)
        {
            diagnostics.Add("active:sdp=timeout");
            return null;
        }

        var result = await sdp.ConfigureAwait(false);
        diagnostics.Add($"active:sdpHits={result.Services.Count}");
        if (result.Services.Count == 0)
        {
            return null;
        }

        var service = result.Services[0];
        var socket = new StreamSocket();
        try
        {
            var connect = socket.ConnectAsync(
                service.ConnectionHostName,
                service.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask();

            if (await Task.WhenAny(connect, Task.Delay(ConnectTimeoutMs)).ConfigureAwait(false) != connect)
            {
                diagnostics.Add("active:connect=timeout");
                socket.Dispose();
                return null;
            }

            await connect.ConfigureAwait(false);
            diagnostics.Add("active:connect=ok");
            return socket;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"active:connect=failed {ex.GetType().Name}");
            socket.Dispose();
            return null;
        }
    }

    internal static async Task SendAsync(DataWriter writer, byte[] payload, List<string> diagnostics)
    {
        writer.WriteBytes(payload);
        var store = writer.StoreAsync().AsTask();
        var delay = Task.Delay(SendTimeoutMs);
        if (await Task.WhenAny(store, delay).ConfigureAwait(false) != store)
        {
            diagnostics.Add($"active:send=timeout({SendTimeoutMs}ms)");
            return;
        }

        await store.ConfigureAwait(false);
    }

    /// <summary>把主机地址规范为与广播路径一致的 <c>XX:XX:…</c> 大写形式。</summary>
    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "unknown";
        }

        var parts = host.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(':', parts.Select(p => p.Trim().ToUpperInvariant()));
    }

    /// <summary>
    /// Socket 输入流读取器。
    /// </summary>
    /// <remarks>
    /// WinRT 的 <see cref="DataReader"/> 不允许同时存在多个挂起的 <c>LoadAsync</c>
    /// （否则报 COMException 0x800710DD），故任何时刻至多保留一个挂起读。
    /// </remarks>
    internal sealed class SocketReader : IDisposable
    {
        private readonly DataReader _reader;
        private Task<uint>? _pending;
        private bool _disposed;

        public SocketReader(IInputStream inputStream)
            => _reader = new DataReader(inputStream) { InputStreamOptions = InputStreamOptions.Partial };

    /// <summary>
    /// 读取一段数据，并区分「超时」与「对端关闭」。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要它</b>：<see cref="CollectAsync"/> 把超时与读到 0 字节都返回空数组，
    /// 二者无法区分。常驻会话<b>必须</b>区分——实测（协议笔记 §23.2）耳机断电时
    /// socket 正是以「读到 0 字节」结束，这是判定断连的主要依据；
    /// 若与超时混淆，就只能靠超时兜底，检测会晚很多。</para>
    /// <para>本方法为新增，不改动 <see cref="CollectAsync"/>，
    /// 已验证的主动读取路径行为不变。</para>
    /// </remarks>
    public async Task<byte[]?> ReadAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return null;
        }

        _pending ??= _reader.LoadAsync(4096).AsTask();

        var delay = Task.Delay(timeoutMs, cancellationToken);
        var finished = await Task.WhenAny(_pending, delay).ConfigureAwait(false);
        if (finished != _pending)
        {
            // 超时：保留挂起读，下次继续等待同一个任务。
            return null;
        }

        uint loaded;
        try
        {
            loaded = await _pending.ConfigureAwait(false);
        }
        catch
        {
            _pending = null;
            throw;
        }

        _pending = null;

        if (loaded == 0)
        {
            throw new EndOfStreamException("对端关闭连接：socket 输入流读到 0 字节");
        }

        var buffer = new byte[loaded];
        _reader.ReadBytes(buffer);
        return buffer;
    }

    /// <summary>在给定时间窗内读取并拼接全部数据。</summary>
    public async Task<byte[]> CollectAsync(int timeoutMs, CancellationToken cancellationToken)
    {
            if (_disposed)
            {
                return [];
            }

            _pending ??= _reader.LoadAsync(4096).AsTask();

            var delay = Task.Delay(timeoutMs, cancellationToken);
            var finished = await Task.WhenAny(_pending, delay).ConfigureAwait(false);
            if (finished != _pending)
            {
                return [];
            }

            var loaded = await _pending.ConfigureAwait(false);
            _pending = null;

            if (loaded == 0)
            {
                return [];
            }

            var buffer = new byte[loaded];
            _reader.ReadBytes(buffer);
            return buffer;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reader.Dispose();
        }
    }
}
