using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Second stage of the offline pipeline: reassemble classic ISO-TP PDUs from
/// the frames the classifier already vetted. Unlike the online transport there
/// is no timeout and no retransmission — the log is a finished recording, so
/// every anomaly must end up either in a PDU's <c>Warnings</c> or in
/// <see cref="UdsReassemblyResult.ExtraNoiseIndices"/>; nothing is dropped.
/// </summary>
public sealed class OfflineIsoTpReassembler
{
    private const string ReasonIncomplete = "IncompletePdu";
    private const string ReasonNewFirstFrame = "NewFirstFrameOverwroteIncompletePdu";
    private const string ReasonNewSingleFrame = "NewSingleFrameInterruptedIncompletePdu";
    private const string ReasonSequenceError = "SequenceError";

    private sealed class Stream
    {
        public bool Active;
        public int Declared;
        public int NextSeq;
        public readonly List<byte> Data = new();
        public readonly List<int> Evidence = new();
        public readonly List<int> FlowControl = new();
        public double FirstTs;
        public double LastTs;
        public UdsDirectionRole Role;
    }

    public UdsReassemblyResult Reassemble(
        IReadOnlyList<ReplayFrame> frames,
        IReadOnlyList<UdsFrameClassification> classes,
        UdsOfflineAddress address)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(address);
        if (classes.Count != frames.Count)
            throw new ArgumentException("classes and frames must be parallel lists", nameof(classes));

        var pdus = new List<UdsOfflinePdu>();
        var warnings = new List<UdsTransportWarning>();
        var noise = new List<int>();
        // 每个 CAN ID（+方向）一条流；address 已保证方向由 ID 决定。
        var streams = new Dictionary<(ushort Channel, uint Id, bool IsExtended), Stream>();

        Stream StreamFor(uint id, UdsDirectionRole role)
        {
            var key = (address.Channel, id, address.IsExtended);
            if (!streams.TryGetValue(key, out var s))
                streams[key] = s = new Stream { Role = role };
            return s;
        }

        void FlushIncomplete(Stream s, string reason, int warningIndex, double lastTs)
        {
            warnings.Add(new UdsTransportWarning(warningIndex, reason));
            pdus.Add(Build(s, complete: false, lastIndex: s.Evidence[^1], lastTs));
            s.Active = false;
        }

        for (var i = 0; i < frames.Count; i++)
        {
            var c = classes[i];
            if (c.Class != UdsFrameClass.MatchedDiagnostic || c.Role is null) continue;

            var f = frames[i];
            if (!ClassicIsoTpPci.TryParse(f.Data, out var pci)) continue; // classifier 已拦

            var s = StreamFor(f.Id, c.Role.Value);

            switch (pci.Kind)
            {
                case ClassicPciKind.SingleFrame:
                {
                    if (s.Active) FlushIncomplete(s, ReasonNewSingleFrame, s.Evidence[^1], s.LastTs);

                    var payload = f.Data.AsSpan(pci.PayloadOffset, pci.DeclaredLength).ToArray();
                    pdus.Add(new UdsOfflinePdu(
                        s.Role, payload, f.Timestamp, f.Timestamp, i, i,
                        new[] { i }, false, true, Array.Empty<string>()));
                    break;
                }

                case ClassicPciKind.FirstFrame:
                    if (s.Active) FlushIncomplete(s, ReasonNewFirstFrame, s.Evidence[^1], s.LastTs);

                    s = new Stream
                    {
                        Active = true,
                        Declared = pci.DeclaredLength,
                        NextSeq = 1,
                        Role = c.Role.Value,
                        FirstTs = f.Timestamp,
                        LastTs = f.Timestamp,
                    };
                    s.Data.AddRange(f.Data[pci.PayloadOffset..].ToArray());
                    s.Evidence.Add(i);
                    streams[(address.Channel, f.Id, address.IsExtended)] = s;
                    break;

                case ClassicPciKind.ConsecutiveFrame:
                    if (!s.Active)
                    {
                        // 日志从传输中段开始：没有 FF 可挂，必须进噪声桶。
                        noise.Add(i);
                        warnings.Add(new UdsTransportWarning(i, UdsNoiseReason.OrphanConsecutiveFrame.ToString()));
                        break;
                    }
                    if (pci.Sequence != s.NextSeq)
                    {
                        noise.Add(i);
                        warnings.Add(new UdsTransportWarning(i, ReasonSequenceError));
                        pdus.Add(Build(s, complete: false, lastIndex: s.Evidence[^1], lastTs: s.LastTs));
                        s.Active = false;
                        break;
                    }
                    s.NextSeq = (s.NextSeq + 1) & 0x0F;
                    s.Data.AddRange(f.Data[pci.PayloadOffset..].ToArray());
                    s.Evidence.Add(i);
                    s.LastTs = f.Timestamp;
                    if (s.Data.Count >= s.Declared)
                    {
                        pdus.Add(Build(s, complete: true, lastIndex: i, lastTs: f.Timestamp));
                        s.Active = false;
                    }
                    break;

                case ClassicPciKind.FlowControl:
                {
                    // 流控由接收方在对向 ID 上发出，因此要挂到对向那条正在进行的流，
                    // 而不是本帧自己的 ID 流（本 ID 此时没有在途 PDU）。
                    var peerId = f.Id == address.RequestId ? address.ResponseId : address.RequestId;
                    var peerKey = (address.Channel, peerId, address.IsExtended);
                    if (streams.TryGetValue(peerKey, out var peer) && peer.Active)
                        peer.FlowControl.Add(i);
                    break;
                }
            }
        }

        // 日志结束仍活跃的 PDU：保留不完整，不静默丢弃。
        foreach (var s in streams.Values.Where(x => x.Active))
            pdus.Add(Build(s, complete: false, lastIndex: s.Evidence[^1], lastTs: s.LastTs));

        return new(pdus, warnings, noise);
    }

    private static UdsOfflinePdu Build(Stream s, bool complete, int lastIndex, double lastTs)
    {
        var len = Math.Min(s.Declared, s.Data.Count);
        var evidence = s.Evidence.Concat(s.FlowControl).OrderBy(x => x).ToArray();
        return new UdsOfflinePdu(
            s.Role,
            s.Data.Take(len).ToArray(),
            s.FirstTs,
            lastTs,
            evidence[0],
            lastIndex,
            evidence,
            true,
            complete,
            complete ? Array.Empty<string>() : new[] { ReasonIncomplete });
    }
}
