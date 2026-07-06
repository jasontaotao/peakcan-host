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