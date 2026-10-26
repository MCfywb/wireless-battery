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

    /// <summary>单次 poll 的扫描窗口（毫秒）。方案建议 poll 超时 3 秒，此处留出应答余量。</summary>
    private const int PollScanMs = 2500;

    private const int DiscoverScanMs = 3000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var profile = LoadProfile();
        var matcher = new VivoTws3iModelMatcher(profile);
        using var scanner = new BleAdvertisementScanner(CompanyId);

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
                    response = Dispatch(request, matcher, scanner);
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

    private static PluginResponse Dispatch(PluginRequest request, VivoTws3iModelMatcher matcher, BleAdvertisementScanner scanner)
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
        _ = deviceId;
        _ = deviceName;

        var diagnosticCode = percentage is null
            ? adv.Diagnostics.FirstOrDefault(d => d.Contains(componentName, StringComparison.Ordinal))
            : null;

        return new PluginReading
        {
            Component = component,
            Percentage = percentage,
            Charging = charging,
            Connected = connected,
            Quality = percentage is null ? nameof(ReadingQuality.Unavailable) : nameof(ReadingQuality.Reported),
            Source = source,
            DiagnosticCode = diagnosticCode,
        };
    }

    private static IReadOnlyList<byte[]> Scan(BleAdvertisementScanner scanner, int windowMs)
    {
        scanner.Start();
        Thread.Sleep(windowMs);
        scanner.Stop();
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
