namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// SecOC trace badge kinds (spec 2026-09-07 §5-D6.7, Rev5 三态显式化):
/// 受保护帧 join 命中 → Accepted/Rejected；SecOC 已配置但该 CAN ID 不在
/// PDU 列表 → Unprotected；离线/回放源或 SecOC 未接线 → Offline。
/// 禁止"无标注"缺省——未 join 即 Offline。
/// </summary>
public enum SecOcBadgeKind
{
    /// <summary>离线/回放源或 SecOC 未接线（灰）。</summary>
    Offline = 0,

    /// <summary>验签通过（绿）。</summary>
    Accepted = 1,

    /// <summary>验签失败（红，含 reason）。</summary>
    Rejected = 2,

    /// <summary>SecOC 已配置但该帧 CAN ID 未受保护（灰）。</summary>
    Unprotected = 3,
}

/// <summary>
/// One trace row's SecOC badge (join result of the host-side verdict
/// bypass table, spec §5-D6.7). Immutable value; <see cref="TraceEntry"/>
/// holds it behind an INPC property so the badge can land after row
/// creation without recreating the row.
/// </summary>
public readonly record struct SecOcBadge(string Text, SecOcBadgeKind Kind)
{
    /// <summary>缺省徽标：离线/回放源不验签（spec D6.9 身份 (a)）。</summary>
    public static readonly SecOcBadge Offline = new("离线不验", SecOcBadgeKind.Offline);

    /// <summary>SecOC 已配置但该 CAN ID 未受保护。</summary>
    public static readonly SecOcBadge Unprotected = new("未保护", SecOcBadgeKind.Unprotected);

    /// <summary>验签通过徽标。</summary>
    public static readonly SecOcBadge Accepted = new("✓", SecOcBadgeKind.Accepted);

    /// <summary>验签失败徽标（附 reject reason）。</summary>
    public static SecOcBadge Rejected(string reason) => new($"✗ {reason}", SecOcBadgeKind.Rejected);
}
