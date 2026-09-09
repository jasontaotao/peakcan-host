using System.Collections.Concurrent;
using PeakCan.Security.SecOc;

namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>One frame verdict as written by the SecOcChannel RX path.</summary>
public readonly record struct SecOcVerdict(uint CanId, bool Accepted, RejectReason? Reason);

/// <summary>
/// Bypass metadata channel (spec §5-D6.7): (sourceId, frameSeq) → verdict.
/// ReplayFrame is a frozen type and must not carry verdict fields; the trace
/// rendering layer joins against this table instead. Keyed by source bucket;
/// the caller clears a bucket on disconnect / trace session rebuild.
/// </summary>
public sealed class SecOcVerdictTable
{
    /// <summary>Max retained verdicts per source; oldest entries are evicted so a
    /// long run cannot grow memory without bound (review HIGH fix).</summary>
    private const int MaxEntriesPerSource = 4096;

    private readonly ConcurrentDictionary<ushort, ConcurrentDictionary<long, SecOcVerdict>> _entries = new();

    public void Record(ushort sourceHandle, long frameSeq, SecOcVerdict verdict)
    {
        var bucket = _entries.GetOrAdd(sourceHandle, _ => new());
        bucket[frameSeq] = verdict;
        if (bucket.Count > MaxEntriesPerSource)
        {
            // Evict the oldest quarter (seqs are monotonic per source).
            var threshold = frameSeq - MaxEntriesPerSource + (MaxEntriesPerSource / 4);
            foreach (var key in bucket.Keys)
                if (key < threshold)
                    bucket.TryRemove(key, out _);
        }
    }

    public bool TryGet(ushort sourceHandle, long frameSeq, out SecOcVerdict verdict)
    {
        verdict = default;
        return _entries.TryGetValue(sourceHandle, out var bucket) &&
               bucket.TryGetValue(frameSeq, out verdict);
    }

    /// <summary>Drop all verdicts for one source (channel disconnect / session rebuild).</summary>
    public void Clear(ushort sourceHandle) => _entries.TryRemove(sourceHandle, out _);

    /// <summary>Drop everything (global teardown).</summary>
    public void Clear() => _entries.Clear();

    public int Count => _entries.Sum(b => b.Value.Count);
}

/// <summary>Per-canId verdict bucket (spec §5-D6.2).</summary>
public sealed record SecOcStatsBucket(long Accepted, long Rejected, RejectReason? LastReason);

/// <summary>
/// Per-canId verdict aggregation for SecOC observability; global counters feed
/// the HIL assertion layer (Phase 2: expression registry, M2.4).
/// Thread-safe; RX path is the only writer.
/// </summary>
public sealed class SecOcStats : global::PeakCan.Host.Core.HIL.Contracts.ISecOcStats
{
    private readonly ConcurrentDictionary<uint, (long Accepted, long Rejected, RejectReason? LastReason)> _buckets = new();

    public void Record(uint canId, VerifyResult verdict)
    {
        _buckets.AddOrUpdate(canId,
            _ => verdict.Accepted ? (1, 0, null) : (0, 1, verdict.Reason),
            (_, b) => verdict.Accepted ? (b.Accepted + 1, b.Rejected, null) : (b.Accepted, b.Rejected + 1, verdict.Reason));
    }

    public bool TryGet(uint canId, out SecOcStatsBucket bucket)
    {
        if (_buckets.TryGetValue(canId, out var b))
        {
            bucket = new SecOcStatsBucket(b.Accepted, b.Rejected, b.LastReason);
            return true;
        }
        bucket = new SecOcStatsBucket(0, 0, null);
        return false;
    }

    public long TotalAccepted => _buckets.Values.Sum(b => b.Accepted);
    public long TotalRejected => _buckets.Values.Sum(b => b.Rejected);

    bool global::PeakCan.Host.Core.HIL.Contracts.ISecOcStats.TryGet(uint canId,
        out global::PeakCan.Host.Core.HIL.Contracts.SecOcVerdictBucket bucket)
    {
        if (TryGet(canId, out var b))
        {
            bucket = new global::PeakCan.Host.Core.HIL.Contracts.SecOcVerdictBucket(
                b.Accepted, b.Rejected, b.LastReason?.ToString());
            return true;
        }
        bucket = new global::PeakCan.Host.Core.HIL.Contracts.SecOcVerdictBucket(0, 0, null);
        return false;
    }
}
