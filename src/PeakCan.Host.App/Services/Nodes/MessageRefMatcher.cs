namespace PeakCan.Host.App.Services.Nodes;

/// <summary>MessageRef 匹配工具。</summary>
public static class MessageRefMatcher
{
    /// <summary>模式宽容匹配：PGN/Id 相等；Sa/Mode 仅在双方都非空时要求相等（触发与目标查找共用）。</summary>
    public static bool Matches(MessageRef a, MessageRef b) => (a, b) switch
    {
        (J1939MessageRef x, J1939MessageRef y) => x.Pgn == y.Pgn
            && (x.Sa is null || y.Sa is null || x.Sa == y.Sa)
            && (x.Mode is null || y.Mode is null || x.Mode == y.Mode),
        (CanMessageRef x, CanMessageRef y) => x.Id == y.Id && x.IsExtended == y.IsExtended,
        _ => false,
    };
}
