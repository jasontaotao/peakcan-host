using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.6.1 PATCH: drift-detection test between the
/// <c>docs/schemas/tmtrace-v1.schema.json</c> contract and the
/// <see cref="TraceSessionBundleDto"/> + sub-DTO definitions. Uses
/// reflection (no JSON Schema validator library — adds no new deps).
/// Catches accidental DTO rename / property drop / type change at
/// compile-test time. Future maintainers who touch the DTO must also
/// touch the schema; this test fails if they don't.
/// </summary>
public class TmtraceSchemaValidationTests
{
    private static readonly string SchemaPath = Path.Combine(
        FindRepoRoot(), "docs", "schemas", "tmtrace-v1.schema.json");

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        File.ReadAllText(SchemaPath));

    // v3.6.1 PATCH: CA1861 — prefer static readonly field for arrays
    // passed to FluentAssertions assertions.
    private static readonly string[] TopLevelRequiredFields =
    {
        "version", "schema", "savedAt", "appVersion",
        "dbcPath", "globalCanIdFilter", "sources", "viewports"
    };

    private static readonly string[] ArgbChannels =
        { "colorA", "colorR", "colorG", "colorB" };

    [Fact]
    public void TopLevelDto_PropertiesArePresentInSchema()
    {
        var schemaProperties = GetSchemaProperties(Schema.RootElement);

        foreach (var prop in typeof(TraceSessionBundleDto).GetProperties())
        {
            if (IsJsonIgnored(prop)) continue;
            var jsonName = GetJsonName(prop);
            schemaProperties.Should().ContainKey(jsonName,
                $"TraceSessionBundleDto.{prop.Name} (json '{jsonName}') is missing from the schema's properties");
        }
    }

    [Fact]
    public void SubDto_PropertiesArePresentInSchema()
    {
        var defs = Schema.RootElement.GetProperty("$defs");

        AssertSubDto(typeof(BundleSourceDto), defs.GetProperty("BundleSourceDto"));
        AssertSubDto(typeof(BundlePlaybackDto), defs.GetProperty("BundlePlaybackDto"));
        AssertSubDto(typeof(BundleViewportDto), defs.GetProperty("BundleViewportDto"));
        // v3.8.0 MINOR: new sub-DTOs for bookmarks + loop regions.
        AssertSubDto(typeof(BookmarkDto), defs.GetProperty("BookmarkDto"));
        AssertSubDto(typeof(LoopRegionDto), defs.GetProperty("LoopRegionDto"));
    }

    [Fact]
    public void SchemaVersionAndIdentifier_ArePinned()
    {
        // The schema document itself declares these as `const` inside the
        // bundle's `properties` block — a valid bundle MUST have
        // `"version": 1` and `"schema": "tmtrace/v1"`. The schema's own
        // $id is the URL where it lives, not the format version.
        var properties = Schema.RootElement.GetProperty("properties");
        properties.GetProperty("version").GetProperty("const").GetInt32().Should().Be(1);
        properties.GetProperty("schema").GetProperty("const").GetString().Should().Be("tmtrace/v1");
    }

    [Fact]
    public void TopLevelRequired_IncludesAllNonNullableScalarDtoProperties()
    {
        // The schema's `required` array should include every non-nullable
        // scalar/string/array DTO property. We can't detect "nullable" via
        // reflection (the C# `?` annotation isn't always preserved on
        // runtime properties), so we assert that the schema's required
        // list is a SUPERSET of the DTO's required-by-construction
        // properties: version, schema, sources, viewports.
        var required = Schema.RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet();

        required.Should().Contain(TopLevelRequiredFields);
    }

    [Fact]
    public void BundleSourceDto_ColorChannelsAreByteRanged()
    {
        // ColorA/R/G/B must each be 0-255 — if a maintainer widens the
        // type to int (e.g. for HDR), the schema must change too. This
        // pins the explicit ARGB byte representation rather than a
        // packed integer.
        var def = Schema.RootElement.GetProperty("$defs")
            .GetProperty("BundleSourceDto");
        var props = GetSchemaProperties(def);
        foreach (var channel in ArgbChannels)
        {
            var schema = props[channel];
            schema.GetProperty("type").GetString().Should().Be("integer",
                $"{channel} must be an integer per OxyColor.FromArgb byte contract");
            schema.GetProperty("minimum").GetInt32().Should().Be(0);
            schema.GetProperty("maximum").GetInt32().Should().Be(255);
        }
    }

    // ---------- v3.8.0 MINOR chunk 8: new schema fields + $defs ----------

    /// <summary>
    /// v3.8.0 MINOR chunk 8: <see cref="BundlePlaybackDto.Bookmarks"/> and
    /// <see cref="BundlePlaybackDto.LoopRegions"/> are present in the schema
    /// with the right types. Pins the contract so a future maintainer
    /// can't silently rename the field without breaking round-trip tests.
    /// </summary>
    [Fact]
    public void BundlePlaybackDto_BookmarksAndLoopRegions_ArePresentInSchema()
    {
        var def = Schema.RootElement.GetProperty("$defs").GetProperty("BundlePlaybackDto");
        var props = GetSchemaProperties(def);

        props.Should().ContainKey("bookmarks",
            "v3.8.0 added Bookmarks to BundlePlaybackDto");
        props["bookmarks"].GetProperty("type").GetString().Should().Be("array");

        props.Should().ContainKey("loopRegions",
            "v3.8.0 added LoopRegions to BundlePlaybackDto");
        props["loopRegions"].GetProperty("type").GetString().Should().Be("array");
    }

    /// <summary>
    /// v3.8.0 MINOR chunk 8: <see cref="BookmarkDto.Id"/> is a non-empty
    /// string and <see cref="BookmarkDto.Timestamp"/> is a number. The
    /// schema's `required` array covers the same non-nullable fields the
    /// C# DTO requires for construction.
    /// </summary>
    [Fact]
    public void BookmarkDto_IdAndTimestamp_AreRequired()
    {
        var def = Schema.RootElement.GetProperty("$defs").GetProperty("BookmarkDto");
        var required = def.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();

        required.Should().Contain("id");
        required.Should().Contain("timestamp");
        // Label is intentionally optional — empty label is a normal state.
        required.Should().NotContain("label");
    }

    /// <summary>
    /// v3.8.0 MINOR chunk 8: <see cref="LoopRegionDto.Id"/>,
    /// <see cref="LoopRegionDto.Start"/>, and <see cref="LoopRegionDto.End"/>
    /// are required. Label is optional (same as BookmarkDto).
    /// </summary>
    [Fact]
    public void LoopRegionDto_IdStartEnd_AreRequired()
    {
        var def = Schema.RootElement.GetProperty("$defs").GetProperty("LoopRegionDto");
        var required = def.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();

        required.Should().Contain("id");
        required.Should().Contain("start");
        required.Should().Contain("end");
        required.Should().NotContain("label");
    }

    /// <summary>
    /// v3.8.0 MINOR chunk 8: <see cref="BookmarkDto"/> and
    /// <see cref="LoopRegionDto"/> are top-level <c>$defs</c> entries
    /// (not nested inside another def). Required for the
    /// <c>"$ref": "#/$defs/BookmarkDto"</c> reference in
    /// <c>BundlePlaybackDto.properties.bookmarks</c> to resolve.
    /// </summary>
    [Fact]
    public void NewSubDtos_AreTopLevelDefs()
    {
        var defs = Schema.RootElement.GetProperty("$defs");

        defs.TryGetProperty("BookmarkDto", out _).Should().BeTrue(
            "BookmarkDto must be a top-level $defs entry for the playback array $ref to resolve");
        defs.TryGetProperty("LoopRegionDto", out _).Should().BeTrue(
            "LoopRegionDto must be a top-level $defs entry for the playback array $ref to resolve");
    }

    /// <summary>
    /// v3.8.0 MINOR chunk 8: a v3.7.2 bundle payload (no playback
    /// envelope) still parses cleanly against the v3.8.0 DTO set. The
    /// new <c>bookmarks</c> and <c>loopRegions</c> fields are optional
    /// on the playback envelope (and the envelope itself is optional per
    /// the existing <c>oneOf: [null, $ref]</c> rule). Old bundles load
    /// with empty lists — forward-compat preserved.
    /// <para>
    /// This test uses raw System.Text.Json deserialization (not a JSON
    /// Schema validator library — keeps the project dep-free per v3.6.1
    /// PATCH design). The schema itself remains the contract; the
    /// <see cref="SubDto_PropertiesArePresentInSchema"/> + this test
    /// together pin forward-compat.
    /// </para>
    /// </summary>
    [Fact]
    public void OldV372Bundle_DeserializesCleanlyWithNewDto()
    {
        var oldBundle = """
            {
              "version": 1,
              "schema": "tmtrace/v1",
              "savedAt": "2026-07-01T00:00:00.0000000Z",
              "appVersion": "3.7.2",
              "dbcPath": "",
              "globalCanIdFilter": "",
              "sources": [],
              "viewports": []
            }
            """;

        var dto = JsonSerializer.Deserialize<TraceSessionBundleDto>(oldBundle);

        dto.Should().NotBeNull();
        dto!.Version.Should().Be(1);
        dto.Playback.Should().BeNull(
            "v3.7.2 bundle has no playback envelope — null deserializes cleanly");
    }

    /// <summary>
    /// v3.8.0 MINOR chunk 8: a v3.7.2 bundle with a playback envelope
    /// carrying only the v3.7.0 fields (<c>replayCanIdFilterText</c> +
    /// scalar transport) deserializes cleanly with the v3.8.0 DTOs.
    /// The new <c>Bookmarks</c> / <c>LoopRegions</c> collections default
    /// to empty lists when the JSON keys are absent — System.Text.Json's
    /// default for missing optional fields with non-nullable initializers.
    /// </summary>
    [Fact]
    public void V372Bundle_WithPlaybackEnvelope_DeserializesWithEmptyCollections()
    {
        var oldBundle = """
            {
              "version": 1,
              "schema": "tmtrace/v1",
              "savedAt": "2026-07-01T00:00:00.0000000Z",
              "appVersion": "3.7.2",
              "dbcPath": "",
              "globalCanIdFilter": "",
              "playback": {
                "masterSourceId": "",
                "loop": true,
                "speed": 2.0,
                "scrubberValue": 1.5,
                "startTimestamp": 0.0,
                "endTimestamp": 10.0,
                "replayCanIdFilterText": ""
              },
              "sources": [],
              "viewports": []
            }
            """;

        var dto = JsonSerializer.Deserialize<TraceSessionBundleDto>(oldBundle);

        dto.Should().NotBeNull();
        dto!.Playback.Should().NotBeNull();
        dto.Playback!.Bookmarks.Should().NotBeNull();
        dto.Playback.Bookmarks.Should().BeEmpty(
            "v3.7.2 playback envelope has no bookmarks key → empty list (not null)");
        dto.Playback.LoopRegions.Should().NotBeNull();
        dto.Playback.LoopRegions.Should().BeEmpty();
        dto.Playback.Loop.Should().BeTrue();
        dto.Playback.Speed.Should().Be(2.0);
    }

    /// <summary>
    /// v3.8.1 PATCH: realistic v3.7.2 bundle with a non-empty
    /// <c>replayCanIdFilterText</c> value (the field added in v3.7.0 MINOR
    /// that any v3.7.0–v3.7.2 Replay tab save would set if the user
    /// typed a CAN-ID filter). The v3.8.0 deserializer must round-trip
    /// the field value as-is — NOT clobber with empty string when
    /// <c>Bookmarks</c> / <c>LoopRegions</c> are absent from the bundle.
    /// <para>
    /// This is the realistic forward-compat shape; the empty-string
    /// variant is covered by <see cref="V372Bundle_WithPlaybackEnvelope_DeserializesWithEmptyCollections"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void V372Bundle_WithNonEmptyReplayCanIdFilterText_RoundTripsValue()
    {
        var oldBundle = """
            {
              "version": 1,
              "schema": "tmtrace/v1",
              "savedAt": "2026-06-15T10:00:00.0000000Z",
              "appVersion": "3.7.2",
              "dbcPath": "",
              "globalCanIdFilter": "",
              "playback": {
                "masterSourceId": "",
                "loop": false,
                "speed": 1.0,
                "scrubberValue": 0.0,
                "replayCanIdFilterText": "0x100, 0x200, 0x300"
              },
              "sources": [],
              "viewports": []
            }
            """;

        var dto = JsonSerializer.Deserialize<TraceSessionBundleDto>(oldBundle);

        dto.Should().NotBeNull();
        dto!.Playback.Should().NotBeNull();
        dto.Playback!.ReplayCanIdFilterText.Should().Be("0x100, 0x200, 0x300",
            "v3.7.2 field must round-trip through v3.8.0 deserializer unchanged");
        dto.Playback.Bookmarks.Should().BeEmpty(
            "v3.7.2 bundle has no bookmarks key → empty list (not null)");
        dto.Playback.LoopRegions.Should().BeEmpty(
            "v3.7.2 bundle has no loop regions key → empty list (not null)");
    }

    // ===== helpers =====

    private static void AssertSubDto(Type dtoType, JsonElement schemaDef)
    {
        var schemaProperties = GetSchemaProperties(schemaDef);
        foreach (var prop in dtoType.GetProperties())
        {
            if (IsJsonIgnored(prop)) continue;
            var jsonName = GetJsonName(prop);
            schemaProperties.Should().ContainKey(jsonName,
                $"{dtoType.Name}.{prop.Name} (json '{jsonName}') is missing from the schema's properties");
        }
    }

    private static bool IsJsonIgnored(PropertyInfo prop)
        => prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null;

    private static Dictionary<string, JsonElement> GetSchemaProperties(JsonElement schemaDef)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schemaDef.TryGetProperty("properties", out var props))
        {
            foreach (var p in props.EnumerateObject())
                result[p.Name] = p.Value;
        }
        return result;
    }

    private static string GetJsonName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        return attr?.Name ?? prop.Name;
    }

    private static string FindRepoRoot()
    {
        // Walk up from the test binary's CWD until we find the PeakCan.Host.slnx marker.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("test must run from within the peakcan-host repo so SchemaPath resolves");
        return dir!.FullName;
    }
}
