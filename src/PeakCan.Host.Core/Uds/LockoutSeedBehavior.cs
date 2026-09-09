namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Server-side lockout seed behavior (spec 2026-09-07 §6.3 钉死流程).
/// 真实 ECU 两流派均存在，做成配置项以匹配被测对象。
/// </summary>
public enum LockoutSeedBehavior
{
    /// <summary>Delayed 状态内首次 requestSeed → 0x36，其后再请求 → 0x37（默认）。</summary>
    ThirtySixThen37 = 0,

    /// <summary>Delayed 状态内一律 0x37。</summary>
    Always37 = 1,
}
