using PeakCan.Host.Mobile.Core.Chat;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.Tests.Chat.Tools;

/// <summary>Hand-rolled fake of <see cref="IMobileChatToolContext"/> for tool tests.</summary>
public sealed class FakeContext : IMobileChatToolContext
{
    public double? AnchorTimestamp { get; set; }
    public bool HasAnchor => AnchorTimestamp is not null;
    public double CurrentTimestamp { get; set; }
    public double? DurationSeconds { get; set; }
    public bool IsDurationKnown { get; set; }
    public DbcCatalog? Dbc { get; set; }
    public long? TraceId { get; set; } = 1;
    public string SourceName { get; set; } = "test.asc";
    public string? FilterText { get; set; }
    public IReadOnlyList<CachedFrame> FramesBefore { get; set; } = [];
    public bool SeekResult { get; set; } = true;
    public double? LastSeek { get; private set; }

    /// <summary>Preset page returned by <see cref="GetFramesForCanIdAsync"/>
    /// (window-filtered by timestamp before returning).</summary>
    public FramePage? FramesForCanIdPage { get; set; }

    /// <summary>Captured (CanId, TStart, TEnd) of each window query.</summary>
    public List<(uint CanId, double? TStart, double? TEnd)> WindowQueries { get; } = [];

    /// <summary>Preset cache summary returned by <see cref="GetCacheSummaryAsync"/>.</summary>
    public TraceCacheSummary? CacheSummary { get; set; }

    public Task<IReadOnlyList<CachedFrame>> GetFramesBeforeAsync(double timestamp, CancellationToken ct)
        => Task.FromResult(FramesBefore);

    public Task<FramePage> GetFramesForCanIdAsync(uint canId, double? tStart, double? tEnd, CancellationToken ct)
    {
        WindowQueries.Add((canId, tStart, tEnd));
        if (FramesForCanIdPage is null) return Task.FromResult(new FramePage([], false));
        var frames = FramesForCanIdPage.Frames
            .Where(f => (tStart is null || f.Timestamp >= tStart) && (tEnd is null || f.Timestamp <= tEnd))
            .ToList();
        return Task.FromResult(new FramePage(frames, FramesForCanIdPage.HasMore));
    }

    public Task<TraceCacheSummary?> GetCacheSummaryAsync(CancellationToken ct)
        => Task.FromResult(CacheSummary);

    public bool Seek(double timestamp)
    {
        LastSeek = timestamp;
        return SeekResult;
    }
}
