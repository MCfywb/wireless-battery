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

    /// <summary>发现（discover）命令的一次性扫描窗口（毫秒）。</summary>
    /// <remarks>
    /// 仅用于宿主显式「立即发现」时做一次快速扫面；与定时 poll 不同，
    /// poll 不再阻塞等待窗口（见 <see cref="Poll"/>）。
    /// </remarks>
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

    /// <summary>
    /// 主动读取的硬上限（毫秒）：必须小于宿主 <c>ActiveReadTimeout = 90s</c>，
    /// 留出余量让宿主在超时前收到响应。
    /// </summary>
    private const int HardTimeoutMs = 80000;

    /// <summary>常驻会话读数的数据来源标识。</summary>
    private const string ResidentSource = "GAIA-RFCOMM:0x8207:resident";

    /// <summary>BLE 扫描器启动后，判定「无线电尚未就绪」的观察窗口（毫秒）。</summary>
    /// <remarks>
    /// 仅用于在诊断里说明「刚启动、还没收到广播」是正常现象，而非设备真的不在。
    /// </remarks>
    private const int RadioPendingWindowMs = 5000;

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

    /// <summary>
    /// 最近一次被动广播探明的设备 MAC（<c>XX:XX:…</c>）。
    /// </summary>
    /// <remarks>
    /// 主动读取用它做精确地址匹配（经验证本型号经典地址 == BLE 地址），
    /// 规避了「按名字枚举在配对未连接时返回 0」的坑。Worker 是常驻进程，
    /// 设备被托盘显示前必已被动探明，故点击刷新时此处通常有值。
    /// </remarks>
    /// <summary>
    /// 近期广播窗口（毫秒）：在此窗口内收到过受支持广播即视为「在场」。
    /// </summary>
    /// <remarks>
    /// 实测空闲时广播约 10 秒一条，取 30 秒留三倍余量，避免把稀疏广播误判成离线。
    /// </remarks>
    private const int RecentBroadcastWindowMs = 30000;

    /// <summary>系统蓝牙状态探针的采样间隔（毫秒）。</summary>
    private const int PresenceSampleIntervalMs = 1000;

    /// <summary>
    /// 系统蓝牙状态探针的去抖窗口（毫秒）。
    /// </summary>
    /// <remarks>
    /// 2026-10-30 两次真机实测：耳机断电过程中 <c>ConnectionStatus</c> 会<b>瞬时回连一次</b>
    /// （一次 6.04s 断、9.07s 又连；另一次 13.11s 断、30.26s 又连）。
    /// 采信单次瞬时翻转会导致误判，故要求候选状态连续稳定 3 秒才升为稳定状态。
    /// </remarks>
    private const int PresenceDebounceMs = 3000;

    private static string? _lastMac;

    /// <summary>最近一次收到受支持广播的时刻；在线判定的依据之一。</summary>
    private static DateTimeOffset? _lastBroadcastUtc;

    /// <summary>系统蓝牙状态探针（后台采样 + 去抖）。</summary>
    private static BluetoothPresenceProbe? _presence;

    /// <summary>
    /// 常驻 RFCOMM（GAIA）会话：启动即建链并保持，持续接收设备自发推送。
    /// </summary>
    private static GaiaResidentSession? _resident;

    /// <summary>Worker 启动时刻；用于判断「尚未收到广播」是否只是无线电未就绪。</summary>
    private static DateTimeOffset _startedUtc;

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

        // 扫描器常驻：启动后立即开始服务请求，数据随到随收。
        // 刻意不做阻塞式预热，原因有二：
        //   ① 预热会把首个 poll 整体拖后，而这段等待计入宿主的 PollTimeout；
        //   ② 预热期收到的广播若被丢弃，首个 poll 会假报「设备未找到」。
        // Windows 无线电初始化期间收到的广播会自然落在缓冲区，不会丢失。
        scanner.Start();
        if (!scanner.IsRunning && scanner.StartError is not null)
        {
            // BLE 无线电当前不可用：不致命，常驻 RFCOMM 与状态探针仍可用。
            Console.Error.WriteLine($"[warn] {scanner.StartError}");
        }
        _startedUtc = DateTimeOffset.UtcNow;

        // 系统蓝牙状态探针：后台采样 ConnectionStatus，补上「连着本机放歌时广播被压制」的盲区。
        // 只读系统已维护的状态，不发起连接、不写设备，与插件 readOnly 能力声明一致。
        var name = matcher.Profile.BluetoothNamePrefixes.FirstOrDefault() ?? "vivo TWS 3i";
        _presence = new BluetoothPresenceProbe(
            name,
            TimeSpan.FromMilliseconds(PresenceSampleIntervalMs),
            TimeSpan.FromMilliseconds(PresenceDebounceMs));
        _presence.Start();

        // 常驻 RFCOMM 会话：建链、断连检测、退避重连由它自己管；
        // 是否该建链则交给在线判定（设备不在就别盲目重试）。
        _resident = new GaiaResidentSession(name, ComputePresent);
        _resident.Start();

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

        // stdin 关闭（宿主退出 / shutdown）：先停探针与常驻会话，
        // 避免后台任务与已建立的 RFCOMM 连接残留在进程退出路径上。
        _presence?.Dispose();
        _resident?.Dispose();

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
                // BLE 无线电若在启动后才可用，此处顺带恢复扫描（无代价）。
                scanner.TryRecover();
                return await Poll(request.RequestId, matcher, scanner).ConfigureAwait(false);

            case PluginMethods.ActiveRead:
                return await ActiveRead(request, matcher).ConfigureAwait(false);

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
    private static async Task<PluginResponse> ActiveRead(PluginRequest request, VivoTws3iModelMatcher matcher)
    {
        var diagnostics = new List<string>();
        var requestId = request.RequestId;
        var name = matcher.Profile.BluetoothNamePrefixes.FirstOrDefault() ?? "vivo TWS 3i";

        // 优先用被动广播探明的 MAC；缺失时回退到请求携带的 deviceId（形如 bt:XX:XX:…）。
        var knownMac = _lastMac;
        if (string.IsNullOrWhiteSpace(knownMac)
            && !string.IsNullOrWhiteSpace(request.DeviceId)
            && request.DeviceId.StartsWith("bt:", StringComparison.OrdinalIgnoreCase))
        {
            knownMac = request.DeviceId.Substring(3);
        }

        // 常驻会话已连接时不可另建连接：同一设备的同一 RFCOMM 通道只允许一个 socket，
        // 新建必然被系统拒绝（实测 COMException「通常每个套接字地址…只允许使用一次」）。
        // 因此复用常驻会话的最新帧——顺带把「立即刷新」从约 6.8 秒变成瞬间返回。
        if (_resident?.State == ResidentSessionState.Connected)
        {
            var residentBattery = _resident.Latest;
            if (residentBattery is null)
            {
                return Error(requestId, PluginErrorCodes.NoFreshReport,
                    "常驻会话已连接，但耳机自连接以来尚未推送电量帧；"
                    + "0x8207 只在状态变化时推送，可取出/放回耳机制造一次状态变化后再试");
            }

            var residentName = matcher.GetDisplayName(matcher.Profile.ModelIds.FirstOrDefault());
            return new PluginResponse
            {
                RequestId = requestId,
                Ok = true,
                Readings =
                [
                    ResidentReading(residentBattery, residentName, "leftEarbud",
                        residentBattery.Left, null, true),
                    ResidentReading(residentBattery, residentName, "rightEarbud",
                        residentBattery.Right, null, true),
                    ResidentReading(residentBattery, residentName, "chargingCase",
                        residentBattery.Case, residentBattery.CaseCharging, true),
                ],
            };
        }

        // 硬上限：即便某步 WinRT 调用意外挂死（设备处于不可查询状态），
        // 也必须在宿主 90s 超时前返回可控错误，而不是让 UI 干等。
        var readTask = GaiaActiveReader
            .ReadAsync(name, knownMac, ActiveReadListenMs, CancellationToken.None);
        var hardCap = Task.Delay(HardTimeoutMs);
        GaiaActiveReader.GaiaActiveReadResult result;
        if (await Task.WhenAny(readTask, hardCap).ConfigureAwait(false) != readTask)
        {
            diagnostics.Add($"active:hardTimeout={HardTimeoutMs / 1000}s");
            var failed = new PluginResponse
            {
                RequestId = requestId,
                Ok = false,
                Error = new PluginError
                {
                    Code = PluginErrorCodes.InternalError,
                    Message = "主动读取内部超时：设备可能暂时不可查询，请稍后重试，或先断开再重连该蓝牙设备",
                },
            };
            failed.Diagnostics.AddRange(diagnostics);
            return failed;
        }

        result = await readTask.ConfigureAwait(false);
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

        // 主动读取成功本身就是「设备在场」的强证据：刷新广播时间戳，
        // 使后续若干秒内的在线判定不被「广播被压制」误判成离线。
        _lastBroadcastUtc = now;
        if (_presence is not null && result.DeviceId is { Length: > 3 } id)
        {
            await _presence.SetKnownMacAsync(id.Substring(3)).ConfigureAwait(false);
        }

        if (_resident is not null && result.DeviceId is { Length: > 3 } residentId)
        {
            _resident.SetKnownMac(residentId.Substring(3));
        }

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
            // 主动读取走的是「连上设备」路径：能连上、握手成功并收到上报，
            // 设备必然在场，故在线判定恒为 true（同时把系统连接态一并回填）。
            Connected = _presence?.StableConnected,
            Present = true,
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
                if (string.IsNullOrWhiteSpace(_lastMac))
                {
                    _lastMac = adv.MacAddress;
                }
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

    /// <summary>
    /// 在线判定。
    /// </summary>
    /// <remarks>
    /// <para>两条证据<b>互补</b>，缺一不可：</para>
    /// <list type="bullet">
    /// <item><description>近期收到受支持广播——未连本机时的主要依据。</description></item>
    /// <item><description>系统经典蓝牙处于连接态——连着本机放歌时 BLE 广播会被压制，
    /// 此时<b>只有</b>系统连接态能证明设备还在。</description></item>
    /// </list>
    /// <para>两者都否定时才判离线；系统状态未知（<c>null</c>）时返回 <c>null</c>（不判定），
    /// 宿主保持原有行为，避免把「探针还没就绪」误当成「设备走了」。</para>
    /// </remarks>
    private static bool? ComputePresent()
    {
        if (_lastBroadcastUtc is { } last
            && (DateTimeOffset.UtcNow - last).TotalMilliseconds < RecentBroadcastWindowMs)
        {
            return true;
        }

        return _presence?.StableConnected;
    }

    private static async Task<PluginResponse> Poll(string requestId, VivoTws3iModelMatcher matcher, BleAdvertisementScanner scanner)
    {
        // 持续监听：扫描器在 Worker 启动后即常驻且事件驱动，会累积所有收到的广播。
        // Poll 只取「自上次取走以来」累积的载荷并立即返回，不再阻塞等待固定窗口。
        // 这样每条广播最多在下一个轮询周期（宿主默认 5 秒）内被取到，
        // 命中率与响应速度都优于原先的固定 12 秒窗口——窗口期间到达的广播不会再被「等窗口」拖慢。
        var payloads = scanner.Drain();
        var diagnostics = new List<string>();

        // 刚启动且缓冲区为空：多半是 Windows 无线电尚未就绪，不是设备真的不在。
        // 明确写进诊断，避免用户把启动瞬间的「未找到」误当成设备离线。
        if (payloads.Count == 0)
        {
            var startupMs = (DateTimeOffset.UtcNow - _startedUtc).TotalMilliseconds;
            if (startupMs < RadioPendingWindowMs)
            {
                diagnostics.Add($"scanner:radioPending(startupMs={startupMs:F0})");
            }
        }

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
            _lastMac = adv.MacAddress;
            _lastBroadcastUtc = DateTimeOffset.UtcNow;

            // 拿到 MAC 后让探针改用按地址精确匹配（比按名字枚举可靠）。
            if (_presence is not null)
            {
                await _presence.SetKnownMacAsync(_lastMac).ConfigureAwait(false);
            }

            // 常驻会话同样改用按地址匹配（首帧广播后才有 MAC，此前走名字兜底）。
            _resident?.SetKnownMac(_lastMac);

            break;
        }

        // 常驻会话的诊断（连接/断连/重连）一并带回，便于在诊断窗口里看到它的状态。
        if (_resident is not null)
        {
            diagnostics.AddRange(_resident.DrainDiagnostics());
        }

        if (chosen is null)
        {
            // 常驻会话有电量时优先用它：这是真实的实时数据，
            // 且正是「连着本机放歌时 BLE 广播被压制」场景下的唯一数据源。
            if (_resident?.Latest is { } residentBattery
                && _resident.State == ResidentSessionState.Connected)
            {
                var stalePresent = ComputePresent();
                var residentDeviceName = matcher.GetDisplayName(matcher.Profile.ModelIds.FirstOrDefault());
                var residentReadings = new List<PluginReading>
                {
                    ResidentReading(residentBattery, residentDeviceName, "leftEarbud",
                        residentBattery.Left, null, stalePresent),
                    ResidentReading(residentBattery, residentDeviceName, "rightEarbud",
                        residentBattery.Right, null, stalePresent),
                    ResidentReading(residentBattery, residentDeviceName, "chargingCase",
                        residentBattery.Case, residentBattery.CaseCharging, stalePresent),
                };

                _lastReadings = residentReadings;
                _lastSuccessUtc = DateTimeOffset.UtcNow;

                return new PluginResponse
                {
                    RequestId = requestId,
                    Ok = true,
                    Readings = residentReadings,
                    Diagnostics = diagnostics,
                };
            }

            // 广播频率可能极低：若有历史成功读数，按 §4.2 返回 Stale 而非「未找到」。
            if (_lastReadings is not null && _lastSuccessUtc is not null)
            {
                // 必须保留 DeviceId/DeviceName：宿主按设备标识归并读数，
                // 标识一旦丢失就会被回退成「{插件 id}:default」，
                // 在真实设备旁边凭空多出一台幽灵设备（托盘显示「共 2 台设备」）。
                // 本轮没抓到新广播，正是「设备是不是走了」最需要回答的时刻：
                // 必须把在线判定一并带回去，否则宿主无从知道该不该移除这台设备。
                var stalePresent = ComputePresent();
                diagnostics.Add($"presence:present={(stalePresent is null ? "unknown" : stalePresent.Value.ToString())}"
                    + $" bleRecent={_lastBroadcastUtc is not null} bt={_presence?.LastRawStatus ?? "?"}");

                var stale = new List<PluginReading>(_lastReadings.Count);
                foreach (var r in _lastReadings)
                {
                    stale.Add(r.MarkStale("NO_FRESH_ADVERTISEMENT_IN_WINDOW") with { Present = stalePresent });
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

        // 本轮抓到了广播，设备必然在场；同时把系统连接态一并回填（Connected 字段此前恒为 null）。
        var present = ComputePresent();
        var connected = ResolveConnected(chosen.ConnectedState);

        var readings = new List<PluginReading>
        {
            BuildReading(deviceId, deviceName, "leftEarbud", chosen.LeftEarbudBattery,
                chosen.LeftEarbudCharging, connected, chosen, now, source, "左耳", present),
            BuildReading(deviceId, deviceName, "rightEarbud", chosen.RightEarbudBattery,
                chosen.RightEarbudCharging, connected, chosen, now, source, "右耳", present),
            BuildReading(deviceId, deviceName, "chargingCase", chosen.ChargingCaseBattery,
                chosen.ChargingCaseCharging, connected, chosen, now, source, "充电盒", present),
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
        string componentName,
        bool? present)
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
            Present = present,
            Quality = percentage is null ? nameof(ReadingQuality.Unavailable) : nameof(ReadingQuality.Reported),
            Source = source,
            DiagnosticCode = diagnosticCode,
        };
    }

    /// <summary>构造一条来自常驻会话的读数。</summary>
    /// <remarks>
    /// 与主动读取路径产出<b>同一个 deviceId</b>，保证宿主把两路读数归并到同一台设备；
    /// 只在来源标识上区分（<c>:resident</c>），便于诊断时看清数据是哪条路来的。
    /// </remarks>
    private static PluginReading ResidentReading(
        ResidentBattery battery,
        string deviceName,
        string component,
        int? percentage,
        bool? charging,
        bool? present)
        => new()
        {
            DeviceId = battery.DeviceId,
            DeviceName = deviceName,
            Component = component,
            Percentage = percentage,
            Charging = charging,
            Connected = _presence?.StableConnected,
            Present = present,
            Quality = percentage is null
                ? nameof(ReadingQuality.Unavailable)
                : nameof(ReadingQuality.Reported),
            Source = ResidentSource,
        };

    /// <summary>
    /// 连接态取值：优先用系统蓝牙连接态（去抖后），未判定时回退广播自带的连接位。
    /// </summary>
    /// <remarks>
    /// 广播里的 <c>ConnectedState</c> 是耳机自报的配对状态位，只表示「连着某个客户端」，
    /// 不区分连的是电脑还是手机；系统侧的状态则明确指「与本机是否连接」，更准确。
    /// </remarks>
    private static bool? ResolveConnected(bool? broadcastConnected)
        => _presence?.StableConnected ?? broadcastConnected;

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
