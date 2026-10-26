using System.Text;
using System.Text.Json;
using PeripheralBattery.PluginSdk;
using VivoTws3i.Protocol;

namespace VivoTws3i.Worker;

/// <summary>
/// vivo TWS 3i 电量插件 Worker。
/// </summary>
/// <remarks>
/// <para>与宿主通过 stdin/stdout 的 JSON Lines 通信（docs/development-plan.md §5.3）。</para>
/// <para><b>只读</b>：仅被动扫描 BLE 广播，不连接设备、不写任何数据。</para>
/// </remarks>
internal static class Program
{
    private const ushort CompanyId = 0x0837;

    /// <summary>
    /// 单次 poll 的扫描窗口（毫秒）。
    /// </summary>
    /// <remarks>
    /// 方案建议 poll 超时 3 秒，但实测耳机的 Fast Pair 广播在连接稳定后
    /// 会降到约 10 秒一条，3 秒窗口经常扫不到新数据（此时返回 Stale 而非未找到）。
    /// 这里取 12 秒以提高命中率；宿主侧的超时分级由 PluginHost 负责。
    /// </remarks>
    private const int PollScanMs = 12000;

    private const int DiscoverScanMs = 3000;

    /// <summary>
    /// 主动读取建链后等待上报的窗口（毫秒）。
    /// </summary>
    /// <remarks>
    /// <para><c>0x8207</c> <b>只在耳机状态变化时推送</b>，静止时永不推送（§21）。
    /// 实测首帧到达时间差异极大：一次在注册后立即到达，另一次等了 <b>84 秒</b>
    /// （由用户操作耳机触发）——因此窗口再长也无法保证拿到新值，只能提高命中概率。</para>
    /// <para>取 20 秒的预算理由：SDP 最长 20s + 建链 15s + 握手 3s + 注册链约 2.5s + 监听 20s
    /// ≈ 60s，仍低于宿主的 <c>ActiveReadTimeout = 90s</c>。</para>
    /// </remarks>
    private const int ActiveReadListenMs = 20000;

    /// <summary>BLE 扫描器启动后的预热时间（毫秒），用于等无线电就绪。</summary>
    private const int ScannerWarmupMs = 2500;

    /// <summary>
    /// 最近一次成功读数的缓存。
    /// </summary>
    /// <remarks>
    /// 耳机的 Fast Pair 广播在连接稳定后会降到极低频（实测由约 13 次/秒降至约 10 秒一条），
    /// 因此单次 poll 窗口很可能扫不到新数据。此时按 docs/development-plan.md §4.2
    /// 返回 <c>Stale</c> 质量与上次成功时间，而不是误报「设备未找到」——
    /// 后者会让 UI 把「暂时没刷新」显示成「设备消失」。
    /// </remarks>
    private static List<PluginReading>? _lastReadings;

    private static DateTimeOffset? _lastSuccessUtc;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var profile = LoadProfile();
        var matcher = new VivoTws3iModelMatcher(profile);
        using var scanner = new BleAdvertisementScanner(CompanyId);

        // 扫描器常驻：先开启并预热，等 Windows 无线电就绪后再开始处理请求。
        scanner.Start();
        Thread.Sleep(ScannerWarmupMs);
        scanner.Drain();

        string? inputLine;
        while ((inputLine = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(inputLine))
            {
                continue;
            }

            PluginResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<PluginRequest>(inputLine, JsonOptions);
                if (request is null)
                {
                    response = Error(string.Empty, PluginErrorCodes.InternalError, "请求反序列化失败");
                }
                else
                {
                    response = await Dispatch(request, matcher, scanner).ConfigureAwait(false);
                }
            }
            catch (JsonException ex)
            {
                response = Error(string.Empty, PluginErrorCodes.InternalError, $"JSON 解析失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                response = Error(string.Empty, PluginErrorCodes.InternalError, $"{ex.GetType().Name}: {ex.Message}");
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            Console.Out.Flush();

            if (response.Ok && response.Diagnostics.Count > 0)
            {
                // stderr 仅用于诊断日志；stdout 必须只有 JSON（§5.3）
                foreach (var d in response.Diagnostics)
                {
                    Console.Error.WriteLine(d);
                }
            }
        }

        return 0;
    }

    private static async Task<PluginResponse> Dispatch(PluginRequest request, VivoTws3iModelMatcher matcher, BleAdvertisementScanner scanner)
    {
        if (request.ProtocolVersion != PluginProtocol.ProtocolVersion)
        {
            return Error(request.RequestId, PluginErrorCodes.ProtocolMismatch,
                $"不支持的协议版本 {request.ProtocolVersion}");
        }

        switch (request.Method)
        {
            case PluginMethods.Discover:
                return Discover(request.RequestId, matcher, scanner);

            case PluginMethods.Poll:
                return Poll(request.RequestId, matcher, scanner);

            case PluginMethods.ActiveRead:
                return await ActiveRead(request.RequestId, matcher).ConfigureAwait(false);

            case PluginMethods.Diagnostics:
                return new PluginResponse
                {
                    RequestId = request.RequestId,
                    Ok = true,
                    Diagnostics =
                    [
                        $"plugin=vivo-tws-3i",
                        $"companyId=0x{CompanyId:X4}",
                        $"transport=ble-advertisement(readonly)",
                        $"profileModelIds={string.Join(',', matcher.Profile.ModelIds)}",
                        $"knownButUnverifiedModelIds={string.Join(',', matcher.Profile.KnownButUnverifiedModelIds)}",
                    ],
                };

            case PluginMethods.Shutdown:
                return new PluginResponse { RequestId = request.RequestId, Ok = true };

            default:
                return Error(request.RequestId, PluginErrorCodes.UnsupportedRequest,
                    $"不支持的方法：{request.Method}");
        }
    }

    /// <summary>
    /// 按需主动读取：只在用户**显式点击「立即刷新」**时由宿主调用（D-010）。
    /// </summary>
    /// <remarks>
    /// <para>机理是「连入触发上报」而不是「发查询命令」：<c>0x0207</c> 在本型号上 4 种
    /// ver/vendor 组合全部无响应，且官方 App 从不发送它。实测可靠的路径是
    /// <b>连接 + 注册链之后设备立即上报一帧 <c>0x8207</c></b>（§21），期间状态变化还会追加推送。
    /// 因此这里建链后短监听一段，取最新的电量帧再断开。</para>
    /// <para>与被动广播路径产出<b>同一个 deviceId</b>（<c>bt:XX:…</c>），保证宿主把两路读数归并到同一台设备。</para>
    /// <para>充电位只解码**盒**（bit2）：这是唯一经双通道交叉验证过的位（§22）。
    /// 左右耳充电位（bit0/bit1）语义未验证，一律留未知。</para>
    /// </remarks>
    private static async Task<PluginResponse> ActiveRead(string requestId, VivoTws3iModelMatcher matcher)
    {
        var diagnostics = new List<string>();
        var name = matcher.Profile.BluetoothNamePrefixes.FirstOrDefault() ?? "vivo TWS 3i";

        var result = await GaiaActiveReader
            .ReadAsync(name, ActiveReadListenMs, CancellationToken.None)
            .ConfigureAwait(false);

        diagnostics.AddRange(result.Diagnostics);

        if (!result.Ok || result.Battery is null || result.DeviceId is null)
        {
            var failed = new PluginResponse
            {
                RequestId = requestId,
                Ok = false,
                Error = new PluginError
                {
                    Code = result.ErrorCode ?? PluginErrorCodes.DeviceNotFound,
                    Message = result.ErrorMessage ?? "主动读取失败",
                },
            };
            failed.Diagnostics.AddRange(diagnostics);
            return failed;
        }

        var now = DateTimeOffset.UtcNow;
        const string source = "GAIA-RFCOMM:0x8207";
        var displayName = matcher.GetDisplayName(matcher.Profile.ModelIds.FirstOrDefault());
        var battery = result.Battery;

        return new PluginResponse
        {
            RequestId = requestId,
            Ok = true,
            Readings =
            [
                ActiveReading(result.DeviceId, displayName, "leftEarbud", battery.Left, null, now, source),
                ActiveReading(result.DeviceId, displayName, "rightEarbud", battery.Right, null, now, source),

                // 只有盒的充电状态是经过验证的；左右耳充电位语义未验证，一律留未知。
                ActiveReading(result.DeviceId, displayName, "chargingCase", battery.Case,
                    battery.CaseCharging, now, source),
            ],
            Diagnostics = diagnostics,
        };
    }

    private static PluginReading ActiveReading(
        string deviceId,
        string deviceName,
        string component,
        int? percentage,
        bool? charging,
        DateTimeOffset now,
        string source)
        => new()
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Component = component,
            Percentage = percentage,
            Charging = charging,
            Connected = null,
            Quality = percentage is null
                ? nameof(ReadingQuality.Unavailable)
                : nameof(ReadingQuality.Reported),
            Source = source,
        };

    private static PluginResponse Discover(string requestId, VivoTws3iModelMatcher matcher, BleAdvertisementScanner scanner)
    {
        var payloads = Scan(scanner, DiscoverScanMs);
        var diagnostics = new List<string>();
        var devices = new List<(string Mac, int ModelId, VivoTwsAdvertisementFormat Format)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var payload in payloads)
        {
            if (!VivoTwsAdvertisementParser.TryParse(payload, out var adv, out _)
                || adv is null)
            {
                continue;
            }

            var verdict = matcher.Evaluate(adv.ModelId);
            if (verdict != ModelMatchVerdict.Supported)
            {
                diagnostics.Add($"{VivoTwsDiagnostics.ModelNotSupported}:model={adv.ModelId} verdict={verdict}");
                continue;
            }

            if (seen.Add(adv.MacAddress))
            {
                devices.Add((adv.MacAddress, adv.ModelId, adv.Format));
            }
        }

        var response = new PluginResponse { RequestId = requestId, Ok = true };
        foreach (var (mac, modelId, format) in devices)
        {
            response.Diagnostics.Add($"device:bt:{mac} model={modelId} format={format}");
        }

        response.Diagnostics.AddRange(diagnostics);
        return response;
    }

    private static PluginResponse Poll(string requestId, VivoTws3iModelMatcher matcher, BleAdvertisementScanner scanner)
    {
        var payloads = Scan(scanner, PollScanMs);
        var diagnostics = new List<string>();

        VivoTwsAdvertisement? chosen = null;
        foreach (var payload in payloads)
        {
            if (!VivoTwsAdvertisementParser.TryParse(payload, out var adv, out var status)
                || adv is null)
            {
                if (status != VivoTwsParseStatus.EmptyPayload)
                {
                    diagnostics.Add($"parse:{status}");
                }

                continue;
            }

            if (!matcher.IsSupported(adv.ModelId))
            {
                diagnostics.Add($"{VivoTwsDiagnostics.ModelNotSupported}:model={adv.ModelId}");
                continue;
            }

            chosen = adv;
            break;
        }

        if (chosen is null)
        {
            // 广播频率可能极低：若有历史成功读数，按 §4.2 返回 Stale 而非「未找到」。
            if (_lastReadings is not null && _lastSuccessUtc is not null)
            {
                // 必须保留 DeviceId/DeviceName：宿主按设备标识归并读数，
                // 标识一旦丢失就会被回退成「{插件 id}:default」，
                // 在真实设备旁边凭空多出一台幽灵设备（托盘显示「共 2 台设备」）。
                var stale = new List<PluginReading>(_lastReadings.Count);
                foreach (var r in _lastReadings)
                {
                    stale.Add(r.MarkStale("NO_FRESH_ADVERTISEMENT_IN_WINDOW"));
                }

                diagnostics.Add($"stale:lastSuccessUtc={_lastSuccessUtc:O}");
                return new PluginResponse
                {
                    RequestId = requestId,
                    Ok = true,
                    Readings = stale,
                    Diagnostics = diagnostics,
                };
            }

            var notFound = new PluginResponse
            {
                RequestId = requestId,
                Ok = false,
                Error = new PluginError
                {
                    Code = PluginErrorCodes.DeviceNotFound,
                    Message = $"扫描窗口内未收到受支持型号的广播（收到 {payloads.Count} 条候选载荷）",
                },
            };
            notFound.Diagnostics.AddRange(diagnostics);
            return notFound;
        }

        diagnostics.AddRange(chosen.Diagnostics);

        var now = DateTimeOffset.UtcNow;
        var deviceId = $"bt:{chosen.MacAddress}";
        var deviceName = matcher.GetDisplayName(chosen.ModelId);
        const string source = "BLE-Advertisement:0x0837";

        var readings = new List<PluginReading>
        {
            BuildReading(deviceId, deviceName, "leftEarbud", chosen.LeftEarbudBattery,
                chosen.LeftEarbudCharging, chosen.ConnectedState, chosen, now, source, "左耳"),
            BuildReading(deviceId, deviceName, "rightEarbud", chosen.RightEarbudBattery,
                chosen.RightEarbudCharging, chosen.ConnectedState, chosen, now, source, "右耳"),
            BuildReading(deviceId, deviceName, "chargingCase", chosen.ChargingCaseBattery,
                chosen.ChargingCaseCharging, chosen.ConnectedState, chosen, now, source, "充电盒"),
        };

        _lastReadings = readings;
        _lastSuccessUtc = now;

        return new PluginResponse
        {
            RequestId = requestId,
            Ok = true,
            Readings = readings,
            Diagnostics = diagnostics,
        };
    }

    private static PluginReading BuildReading(
        string deviceId,
        string deviceName,
        string component,
        int? percentage,
        bool? charging,
        bool? connected,
        VivoTwsAdvertisement adv,
        DateTimeOffset now,
        string source,
        string componentName)
    {
        var diagnosticCode = percentage is null
            ? adv.Diagnostics.FirstOrDefault(d => d.Contains(componentName, StringComparison.Ordinal))
            : null;

        return new PluginReading
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Component = component,
            Percentage = percentage,
            Charging = charging,
            Connected = connected,
            Quality = percentage is null ? nameof(ReadingQuality.Unavailable) : nameof(ReadingQuality.Reported),
            Source = source,
            DiagnosticCode = diagnosticCode,
        };
    }

    /// <summary>
    /// 在给定时间窗内收集广播。
    /// </summary>
    /// <remarks>
    /// 扫描器在 Worker 启动时即已开启并保持常驻，不在每次请求时 Start/Stop：
    /// Windows 的 BLE 无线电首次初始化耗时可达数秒，若每次 poll 都重启扫描器，
    /// 首个请求会因窗口被初始化过程吃掉而误报「设备未找到」。
    /// Worker 本身是常驻轮询进程，扫描器常驻的功耗影响可接受。
    /// </remarks>
    private static IReadOnlyList<byte[]> Scan(BleAdvertisementScanner scanner, int windowMs)
    {
        Thread.Sleep(windowMs);
        return scanner.Drain();
    }

    private static PluginResponse Error(string requestId, string code, string message)
    {
        return new PluginResponse
        {
            RequestId = requestId,
            Ok = false,
            Error = new PluginError { Code = code, Message = message },
        };
    }

    private static VivoTws3iProfile LoadProfile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profile.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<VivoTws3iProfile>(json, JsonOptions);
                if (profile is not null)
                {
                    return profile;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Console.Error.WriteLine($"profile.json 加载失败，使用内置默认值：{ex.Message}");
            }
        }

        return new VivoTws3iProfile();
    }
}
