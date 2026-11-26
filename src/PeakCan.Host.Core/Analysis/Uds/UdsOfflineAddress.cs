using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// The single request/response pair the offline analysis is scoped to.
/// A frame "matches" only when channel, addressing format and id all agree —
/// an id match on another channel is a different bus, not the same ECU.
/// </summary>
public sealed record UdsOfflineAddress(ushort Channel, uint RequestId, uint ResponseId, bool IsExtended)
{
    public bool Matches(ReplayFrame f)
        => f.Channel == Channel && f.IsExtended == IsExtended
           && (f.Id == RequestId || f.Id == ResponseId);

    public UdsDirectionRole RoleOf(ReplayFrame f)
        => f.Id == RequestId ? UdsDirectionRole.Request : UdsDirectionRole.Response;

    /// <summary>Customer-facing text used by the offline log analysis page.</summary>
    public string Display => $"ch{Channel}  0x{RequestId:X3} → 0x{ResponseId:X3}{(IsExtended ? "  (29-bit)" : "")}";
}
