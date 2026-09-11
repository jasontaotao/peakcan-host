using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.Chat;

/// <summary>
/// Bridge between a mobile chat tool and the <see cref="ViewModels.TraceSessionViewModel"/>
/// state the tools need to read/mutate (single anchor, DBC, cache, seek).
/// Implemented by <c>TraceSessionViewModel</c>; tools inject it and are
/// unit-testable with a fake context (no MAUI required).
/// </summary>
/// <remarks>
/// Unlike the desktop <c>IChatToolContext</c>, there is no watch list / group /
/// alias surface on mobile. Anchor values are read live from the SQLite replay
/// cache (<see cref="GetFramesBeforeAsync"/>) instead of an in-memory snapshot,
/// so the anchor query is async here.
/// <para>
/// <b>Threading:</b> production calls come from the chat loop on the UI thread
/// (<see cref="ViewModels.ChatViewModel"/>, <c>ConfigureAwait(true)</c>).
/// </para>
/// </remarks>
public interface IMobileChatToolContext
{
    /// <summary>Current single-anchor timestamp in seconds. Null = not set.</summary>
    double? AnchorTimestamp { get; }

    /// <summary>True when a single anchor is set.</summary>
    bool HasAnchor { get; }

    /// <summary>Current playback timestamp in seconds. NaN when no player is active.</summary>
    double CurrentTimestamp { get; }

    /// <summary>Trace duration in seconds once the duration scan completes; null otherwise.</summary>
    double? DurationSeconds { get; }

    /// <summary>True when the duration scan has completed and <see cref="DurationSeconds"/> is valid.</summary>
    bool IsDurationKnown { get; }

    /// <summary>Currently loaded DBC catalog, or null.</summary>
    DbcCatalog? Dbc { get; }

    /// <summary>SQLite cache trace id for the open session; null when none.</summary>
    long? TraceId { get; }

    /// <summary>Display name of the open source file (or cached path).</summary>
    string SourceName { get; }

    /// <summary>Current ID/PGN filter text, or null when no filter.</summary>
    string? FilterText { get; }

    /// <summary>Latest cached frame per CAN id at or before <paramref name="timestamp"/>
    /// (zero-order hold snapshot), ordered by can id. Empty when the region is not cached.</summary>
    Task<IReadOnlyList<CachedFrame>> GetFramesBeforeAsync(double timestamp, CancellationToken ct);

    /// <summary>Seek the player to <paramref name="timestamp"/> seconds. Returns false
    /// when no player is active (seek is a no-op).</summary>
    bool Seek(double timestamp);
}
