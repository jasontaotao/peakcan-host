using PeakCan.HIL.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// First stage of the offline pipeline: put every input frame into exactly one
/// of the four buckets. Classification order is fixed by the spec (§7.4):
/// unsupported transport → address mismatch → unparseable PCI → matched.
/// </summary>
public sealed class UdsFrameClassifier
{
    public IReadOnlyList<UdsFrameClassification> Classify(
        IReadOnlyList<ReplayFrame> frames, UdsOfflineAddress address)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(address);

        var result = new List<UdsFrameClassification>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if ((f.Flags & (FrameFlags.Fd | FrameFlags.Rtr | FrameFlags.ErrFrame)) != 0)
            {
                result.Add(new(i, UdsFrameClass.UnsupportedInput, UdsNoiseReason.UnsupportedFrameType));
                continue;
            }
            if (!address.Matches(f))
            {
                result.Add(new(i, UdsFrameClass.NonDiagnostic));
                continue;
            }
            if (!ClassicIsoTpPci.TryParse(f.Data, out _))
            {
                var reason = f.Data.Length == 0 ? UdsNoiseReason.InsufficientDlc : UdsNoiseReason.InvalidPci;
                result.Add(new(i, UdsFrameClass.Noise, reason));
                continue;
            }
            result.Add(new(i, UdsFrameClass.MatchedDiagnostic, null, address.RoleOf(f)));
        }
        return result;
    }
}
