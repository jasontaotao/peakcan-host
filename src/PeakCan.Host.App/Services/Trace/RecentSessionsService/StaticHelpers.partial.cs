// RecentSessionsService/StaticHelpers.partial.cs — W27 T3 (Flow C, 22 LoC)
// Static helpers + on-disk DTO: DefaultPath static helper +
// Envelope inner class (JSON shape for JsonSerializer round-trip).
// Both are tightly coupled to JSON-persistence but logically
// distinct from LoadAsync/Persist lifecycle flow.
//
// Envelope inner class moves here per W21 + W24 + W26 sister
// precedent (inner-classes belong with their related helper
// methods, NOT with main state). Sister of W26 CanApi/Envelope
// inner-record location pattern (also extracted to a per-flow
// partial via "near the method that uses it" grouping).
//
// W23 STRUCT-FABRICATION LESSON: verify Environment.GetFolderPath
// 1-arg signature + Path.Combine 2-arg signature (verified during
// verbatim re-extraction from HEAD).
//
// W27 T3 verbatim re-extracted via `git show HEAD:src/...cs | sed -n '108,129p'`
// per W20 T2 R1 fabrication LESSON (29th application).

using System.IO;
using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.Trace;

public sealed partial class RecentSessionsService
{
    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "recent-sessions.json");
    }

    /// <summary>On-disk shape. Internal — public only so
    /// <see cref="JsonSerializer"/> can serialize it.</summary>
    public sealed class Envelope
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = CurrentSchema;

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("recent")]
        public List<RecentSessionDto> Recent { get; set; } = new();
    }
}
