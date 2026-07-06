using System.Text.Json.Serialization;
using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.5.0 MINOR: on-disk DTOs for the <c>.tmtrace</c> Trace Viewer
/// session bundle. Plain POCOs with explicit <see cref="JsonPropertyNameAttribute"/>
/// attributes — NOT VM-typed — so the bundle format is stable across
/// VM refactors.
/// <para>
/// Schema is <c>tmtrace/v1</c>. Adding fields is non-breaking
/// (deserializer ignores unknown keys); renaming or removing fields is
/// a breaking change and requires bumping the <c>schema</c> string.
/// </para>
/// </summary>
public sealed class TraceSessionBundleDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "tmtrace/v1";

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "";

    [JsonPropertyName("dbcPath")]
    public string DbcPath { get; set; } = "";

    [JsonPropertyName("globalCanIdFilter")]
    public string GlobalCanIdFilter { get; set; } = "";

    [JsonPropertyName("playback")]
    public BundlePlaybackDto? Playback { get; set; }

    [JsonPropertyName("sources")]
    public List<BundleSourceDto> Sources { get; set; } = new();

    [JsonPropertyName("viewports")]
    public List<BundleViewportDto> Viewports { get; set; } = new();
}

/// <summary>One loaded trace in a saved session. Path-reference only —
/// recordings are NOT embedded.</summary>
public sealed class BundleSourceDto
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("colorA")]
    public byte ColorA { get; set; }

    [JsonPropertyName("colorR")]
    public byte ColorR { get; set; }

    [JsonPropertyName("colorG")]
    public byte ColorG { get; set; }

    [JsonPropertyName("colorB")]
    public byte ColorB { get; set; }

    [JsonPropertyName("strokeStyle")]
    public string StrokeStyle { get; set; } = "Solid";

    [JsonPropertyName("canIdFilter")]
    public string CanIdFilter { get; set; } = "";

    /// <summary>
    /// v3.6.4 PATCH: SHA-256 content fingerprint of the <c>.asc</c>
    /// file at <see cref="Path"/>, lowercase hex (64 chars). When the
    /// recorded <c>Path</c> is missing on the consumer's filesystem
    /// (file moved, renamed, or on an unmounted drive), the loader
    /// uses this hash to search the user-known
    /// <c>asc-search-dirs.json</c> directory list for a relocated
    /// copy. Empty string means "hash unknown" (the bundle was
    /// written without hashing, or the writer's file was already
    /// missing at save time) — the loader falls back to the
    /// existing path-only behavior.
    /// </summary>
    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = "";

    /// <summary>Convenience: deserialize the four channel bytes into an
    /// <see cref="OxyColor"/> at consumption time.</summary>
    [JsonIgnore]
    public OxyColor Color => OxyColor.FromArgb(ColorA, ColorR, ColorG, ColorB);
}

/// <summary>Playback cursor + transport state. Always restored to a
/// paused/stopped cursor — never auto-resumes.</summary>
public sealed class BundlePlaybackDto
{
    [JsonPropertyName("masterSourceId")]
    public string MasterSourceId { get; set; } = "";

    [JsonPropertyName("loop")]
    public bool Loop { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;

    [JsonPropertyName("scrubberValue")]
    public double ScrubberValue { get; set; }

    [JsonPropertyName("startTimestamp")]
    public double? StartTimestamp { get; set; }

    [JsonPropertyName("endTimestamp")]
    public double? EndTimestamp { get; set; }

    /// <summary>
    /// v3.7.0 MINOR: Replay-tab CAN-ID filter text (free-form,
    /// comma- or whitespace-separated decimal/hex tokens). Persisted
    /// alongside the playback envelope because the filter is a
    /// transport-state concern for the Replay tab (it affects what
    /// frames are emitted), not a per-source display concern. The
    /// Trace Viewer keeps its filter on
    /// <see cref="BundleSourceDto.CanIdFilter"/> (per-source) and
    /// <see cref="TraceSessionBundleDto.GlobalCanIdFilter"/> (global);
    /// the Replay tab has only the single source, so a sibling field
    /// on the playback envelope is the right shape.
    /// <para>
    /// Default = empty string (= no filter). Forward-compat:
    /// deserializers that don't know this field get an empty string,
    /// which matches "no filter" and is a safe no-op.
    /// </para>
    /// </summary>
    [JsonPropertyName("replayCanIdFilterText")]
    public string ReplayCanIdFilterText { get; set; } = "";

    /// <summary>
    /// v3.8.0 MINOR chunk 4: user-captured timestamps for the Replay
    /// tab. Each bookmark is a (id, timestamp, optional label) tuple
    /// added via Ctrl+B / <c>AddBookmarkCommand</c>. Optional with
    /// empty default — v3.7.2 bundles load with no bookmarks (round-trip
    /// safe via <c>additionalProperties: true</c> schema design from
    /// v3.6.1).
    /// </summary>
    [JsonPropertyName("bookmarks")]
    public List<BookmarkDto> Bookmarks { get; set; } = new();

    /// <summary>
    /// v3.8.0 MINOR chunk 6: named playback windows. See
    /// <see cref="LoopRegionDto"/> for the precedence + partial-rewind
    /// semantics. Empty list = legacy single-region behavior
    /// (Start/End Timestamp bounds).
    /// </summary>
    [JsonPropertyName("loopRegions")]
    public List<LoopRegionDto> LoopRegions { get; set; } = new();
}

/// <summary>
/// v3.8.0 MINOR chunk 4: a single Replay-tab bookmark — a point-in-time
/// marker the user can revisit. Mirrors <see cref="BundleSourceDto.SourceId"/>
/// pattern for id generation (GUID). Timestamp is trace-relative seconds.
/// </summary>
public sealed class BookmarkDto
{
    public BookmarkDto() { }
    public BookmarkDto(string id, double timestamp, string? label)
    {
        Id = id;
        Timestamp = timestamp;
        Label = label;
    }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

/// <summary>
/// v3.8.0 MINOR chunk 6: a named playback window — the cursor wraps to
/// <see cref="Start"/> when reaching <see cref="End"/> AND the user has
/// enabled Loop. Persisted on <see cref="BundlePlaybackDto.LoopRegions"/>
/// alongside <see cref="BundlePlaybackDto.Bookmarks"/>.
/// <para>
/// <b>Precedence rule:</b> when <see cref="BundlePlaybackDto.LoopRegions"/>
/// has entries, the FIRST region overrides
/// <see cref="BundlePlaybackDto.StartTimestamp"/> /
/// <see cref="BundlePlaybackDto.EndTimestamp"/> for the wrap target.
/// Empty list = fall back to existing behavior (cursor wraps to t=0 on EOF).
/// </para>
/// <para>
/// <b>Partial v3.8.0:</b> regions seek to their Start on activation but
/// do NOT yet rewind at End (full A/B loop is v3.9.0 territory). See
/// release notes §"Non-scope (still deferred)".
/// </para>
/// </summary>
public sealed class LoopRegionDto
{
    public LoopRegionDto() { }
    public LoopRegionDto(string id, double start, double end, string? label)
    {
        Id = id;
        Start = start;
        End = end;
        Label = label;
    }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

/// <summary>Per-series chart viewport (X-axis range + focus/collapse).
/// Keyed by <see cref="EffectiveKey"/> (SourceId.SignalKey) to disambiguate
/// multi-trace sessions.</summary>
public sealed class BundleViewportDto
{
    [JsonPropertyName("effectiveKey")]
    public string EffectiveKey { get; set; } = "";

    [JsonPropertyName("xMin")]
    public double XMin { get; set; }

    [JsonPropertyName("xMax")]
    public double XMax { get; set; }

    [JsonPropertyName("isFocused")]
    public bool IsFocused { get; set; }

    [JsonPropertyName("isCollapsed")]
    public bool IsCollapsed { get; set; }
}