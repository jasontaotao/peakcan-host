using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: extracts per-source evidence inside the fault
/// window. Per hard-boundary #6: reads via IFrameSourceProvider.GetFrames
/// (Core-side abstraction over the App-layer ITraceSessionRegistry.GetFrames,
/// which copies at the registry boundary, source-unload-safe). Per
/// hard-boundary #2: does NOT re-decode signals via SignalDecoder — uses
/// the signalKey already produced by the AnchorSnapshot flow. Per
/// hard-boundary #14: produces evidence per source, normalized independently
/// downstream by LocalAnalyzer.</summary>
public class EvidenceExtractor
{
    public IReadOnlyList<FaultAnalysisEvidence> Extract(
        FaultEvent faultEvent,
        AnchorSnapshot snapshot,
        IFrameSourceProvider frameSource,
        DbcDocument? dbc,
        IReadOnlyDictionary<uint, string> dbcIdToSourceIdMap)
    {
        ArgumentNullException.ThrowIfNull(faultEvent);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(frameSource);
        ArgumentNullException.ThrowIfNull(dbcIdToSourceIdMap);

        var result = new List<FaultAnalysisEvidence>();
        int nextId = 1;
        double windowStart = faultEvent.CenterTimestampSeconds - faultEvent.WindowBefore.TotalSeconds;
        double windowEnd = faultEvent.CenterTimestampSeconds + faultEvent.WindowAfter.TotalSeconds;

        // Per spec: per-source independent extraction. Walk every SourceId
        // appearing in the anchor snapshot (covers multi-source sessions).
        var sourceIds = snapshot.Signals.Select(s => s.SourceId).Distinct();

        foreach (var sourceId in sourceIds)
        {
            var frames = frameSource.GetFrames(sourceId);
            if (frames.Count == 0) continue;

            // Window-crop
            var inWindow = frames
                .Where(f => f.Timestamp >= windowStart && f.Timestamp <= windowEnd)
                .ToList();

            // For each frame, extract a "state-transition" evidence entry
            // when the byte[0] (assume signal byte 0 for now) differs from
            // the prior in-window frame. Frame-loss / out-of-range checks
            // are deferred to LocalAnalyzer (which has the full picture).
            byte? prevByte0 = null;
            foreach (var frame in inWindow)
            {
                if (frame.Data.Length == 0) continue;
                byte currByte0 = frame.Data[0];
                if (prevByte0.HasValue && currByte0 != prevByte0.Value)
                {
                    var signalKey = dbcIdToSourceIdMap.TryGetValue(frame.Id, out var prefix)
                        ? $"{prefix}.{sourceId}"
                        : $"0x{frame.Id:X}.UnknownSignal.{sourceId}";
                    result.Add(new FaultAnalysisEvidence(
                        EvidenceId: $"E-{nextId++:D4}",
                        SignalKey: signalKey,
                        SourceId: sourceId,
                        Type: "state-transition",
                        TimestampSeconds: frame.Timestamp,
                        Value: currByte0,
                        EnumText: null,
                        Description: $"byte[0] {prevByte0.Value:X2}→{currByte0:X2} @ {frame.Timestamp:F3}s"));
                }
                prevByte0 = currByte0;
            }
        }

        return result;
    }
}
