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

    public Task<IReadOnlyList<CachedFrame>> GetFramesBeforeAsync(double timestamp, CancellationToken ct)
        => Task.FromResult(FramesBefore);

    public bool Seek(double timestamp)
    {
        LastSeek = timestamp;
        return SeekResult;
    }
}
