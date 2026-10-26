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
    private const string VivoPrivateUuid = "00000837-d102-11e1-9b23-00025b00a5a5";

    /// <summary>握手用版本：本机实测握手必须用 v4（v3 无响应）。</summary>
    private const int HandshakeVersion = 4;

    private const int SdpTimeoutMs = 20000;
    private const int ConnectTimeoutMs = 15000;
    private const int HandshakeResponseMs = 3000;
    private const int RegisterGapMs = 500;

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
    /// <param name="deviceName">经典蓝牙设备名（取自 profile 的蓝牙名前缀）。</param>
    /// <param name="listenMs">建链后等待上报的毫秒数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<GaiaActiveReadResult> ReadAsync(
        string deviceName,
        int listenMs,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();

        var device = await FindDeviceAsync(deviceName, diagnostics).ConfigureAwait(false);
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

            var deviceId = "bt:" + NormalizeHost(socket.Information.RemoteAddress.CanonicalName);
            diagnostics.Add($"active:deviceId={deviceId}");

            // 1) 握手
            var handshake = VivoTwsGaiaFrameReader.Build(
                HandshakeVersion, VivoTwsGaiaFrameReader.VendorGaia,
                VivoTwsGaiaFrameReader.CommandHandshake, []);
            await SendAsync(writer, handshake).ConfigureAwait(false);

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
                await SendAsync(writer, frame).ConfigureAwait(false);
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

                buffer.AddRange(chunk);
                var frames = VivoTwsGaiaFrameReader.Parse(buffer.ToArray());
                if (frames.Count == 0)
                {
                    continue;
                }

                foreach (var frame in frames)
                {
                    if (frame.Command != VivoTwsGaiaFrameReader.CommandBatteryReport)
                    {
                        continue;
                    }

                    battery = VivoTwsGaiaFrameReader.DecodeBattery(frame.Payload);
                    reportCount++;
                }

                // 已消费的帧从缓冲中移除，保留可能的半帧等待续传。
                var consumed = frames.Sum(f => VivoTwsGaiaFrameReader.Build(
                    f.Version, f.Vendor, f.Command, f.Payload, f.Flags).Length);
                if (consumed > 0 && consumed <= buffer.Count)
                {
                    buffer.RemoveRange(0, consumed);
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

    private static async Task<BluetoothDevice?> FindDeviceAsync(
        string deviceName, List<string> diagnostics)
    {
        var infos = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromDeviceName(deviceName)).AsTask().ConfigureAwait(false);

        diagnostics.Add($"active:classicCandidates={infos.Count}");
        if (infos.Count == 0)
        {
            return null;
        }

        return await BluetoothDevice.FromIdAsync(infos[0].Id).AsTask().ConfigureAwait(false);
    }

    private static async Task<StreamSocket?> ConnectAsync(
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

    private static async Task SendAsync(DataWriter writer, byte[] payload)
    {
        writer.WriteBytes(payload);
        await writer.StoreAsync().AsTask().ConfigureAwait(false);
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
    private sealed class SocketReader : IDisposable
    {
        private readonly DataReader _reader;
        private Task<uint>? _pending;
        private bool _disposed;

        public SocketReader(IInputStream inputStream)
            => _reader = new DataReader(inputStream) { InputStreamOptions = InputStreamOptions.Partial };

        /// <summary>等待并取回一段数据；超时返回空。</summary>
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
