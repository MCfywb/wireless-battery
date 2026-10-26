using PeripheralBattery.PluginSdk;

namespace PeripheralBattery.Core;

/// <summary>
/// 一次插件轮询的结果。传输无关：Core 不感知 JSON Lines、进程或超时等 IPC 细节，
/// 这些由 <see cref="IPluginClient"/> 的实现（PluginHost）负责。
/// </summary>
/// <param name="Ok">本次轮询是否成功取得读数。</param>
/// <param name="Readings">读数列表；失败时通常为空。</param>
/// <param name="Diagnostics">诊断文本，供「诊断 / 导出研究包」使用。</param>
/// <param name="ErrorCode">失败时的错误码；成功时为 <c>null</c>。</param>
/// <param name="ErrorMessage">失败时的错误说明；成功时为 <c>null</c>。</param>
public sealed record PluginPollOutcome(
    bool Ok,
    IReadOnlyList<BatteryReading> Readings,
    IReadOnlyList<string> Diagnostics,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    /// <summary>构造失败结果。</summary>
    public static PluginPollOutcome Failure(string errorCode, string errorMessage, IReadOnlyList<string>? diagnostics = null)
        => new(false, [], diagnostics ?? [], errorCode, errorMessage);
}

/// <summary>
/// 插件读取客户端契约。由 PluginHost 提供具体实现，Core 只依赖此接口，
/// 避免 Core 与 PluginHost 互相引用。
/// </summary>
public interface IPluginClient
{
    /// <summary>插件 id（与 manifest 中的 id 一致）。</summary>
    string PluginId { get; }

    /// <summary>执行一次读取。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>轮询结果；实现方不应抛出协议层异常，应转为失败结果。</returns>
    Task<PluginPollOutcome> PollAsync(CancellationToken cancellationToken);
}
