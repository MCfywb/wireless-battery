namespace VivoTws3i.Protocol;

/// <summary>
/// 离散采样值的去抖器：候选值需连续稳定一段时间才升为稳定值。
/// </summary>
/// <remarks>
/// <para><b>为什么要去抖</b>：2026-10-30 两次真机实测均发现，vivo TWS 3i 断电过程中
/// <c>BluetoothDevice.ConnectionStatus</c> 会<b>瞬时回连一次</b>——
/// 一次是 6.04s 断、9.07s 又连，另一次 13.11s 断、30.26s 又连，两次时刻还不一样。
/// 若采信单次瞬时翻转：设备其实已走时会误判在线，设备还在时会误判离线。</para>
/// <para>本类只做纯逻辑、不涉及 IO，因此可离线单测；Worker 侧负责按固定间隔喂采样值。</para>
/// </remarks>
public sealed class PresenceDebouncer
{
    private readonly TimeSpan _debounce;

    private bool? _stable;
    private bool? _candidate;
    private DateTimeOffset _candidateSince;

    /// <summary>构造去抖器。</summary>
    /// <param name="debounce">去抖窗口：候选值需连续稳定这么久才升为稳定值。</param>
    public PresenceDebouncer(TimeSpan debounce)
    {
        if (debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce), "去抖窗口必须为正。");
        }

        _debounce = debounce;
    }

    /// <summary>
    /// 当前稳定值。<c>null</c> 表示尚未形成任何判定。
    /// </summary>
    public bool? Stable => _stable;

    /// <summary>
    /// 喂入一次采样，返回更新后的稳定值。
    /// </summary>
    /// <param name="observed">本次采样值；<c>null</c> 表示采样失败。</param>
    /// <param name="now">采样时刻。</param>
    /// <returns>更新后的稳定值。</returns>
    /// <remarks>
    /// 采样失败（<c>null</c>）时<b>不推进去抖、也不改变已有结论</b>——
    /// 宁可保留上一次判断，也不要因为一次读失败就翻转状态。
    /// </remarks>
    public bool? Update(bool? observed, DateTimeOffset now)
    {
        if (observed is null)
        {
            return _stable;
        }

        if (_candidate is null || _candidate.Value != observed.Value)
        {
            _candidate = observed;
            _candidateSince = now;
            return _stable;
        }

        if (now - _candidateSince >= _debounce)
        {
            _stable = _candidate;
        }

        return _stable;
    }
}
