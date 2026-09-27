using System.Globalization;
using PeakCan.Host.Core.Xcp.Calibration;

namespace PeakCan.Host.Core.Tests.Xcp.Calibration;

/// <summary>
/// S8-T1/T2: variant delta codec (spec D1 = standalone JSON schemaVersion=2, D2 = baseline content fingerprint, D3 = extract only differing entries).
/// Byte-deterministic, diff-stable, fingerprint deterministic.
/// </summary>
public sealed class CalibrationVariantDeltaTests
{
    private static readonly string Sha = "b" + new string('0', 63);
    private static readonly DateTimeOffset FixedAt =
        DateTimeOffset.Parse("2026-09-27T02:00:00Z", CultureInfo.InvariantCulture);

    private static List<CalibrationEntry> BaselineEntries() =>
    [
        new("TorqueMax", 123.5, "Nm", "42F6"),
        new("AlphaK", 0.001, null, null),
        new("BattVolt", 3.7, "V", null),
    ];

    private static List<CalibrationEntry> VariantEntries() =>
    [
        new("TorqueMax", 150.0, "Nm", "4316"),
        new("AlphaK", 0.001, null, null),
        new("BattVolt", 3.7, "V", null),
        new("CabinTemp", 22.5, "\u00b0C", null),
    ];

    private static CalibrationParameterSet Baseline(DateTimeOffset? at = null) =>
        CalibrationParameterSet.Export(BaselineEntries(), Sha, "baseline", at ?? FixedAt);

    private static CalibrationParameterSet Variant(DateTimeOffset? at = null) =>
        CalibrationParameterSet.Export(VariantEntries(), Sha, "variant", at ?? FixedAt);

    private static CalibrationVariantDelta Extract(string? name = null, DateTimeOffset? at = null) =>
        CalibrationVariantDelta.Extract(name ?? "sport", Baseline(), Variant(), "test", at ?? FixedAt);

    // --- Extract ---

    [Fact]
    public void Extract_produces_only_differing_entries()
    {
        var delta = Extract();
        // TorqueMax 123.5 -> 150.0 = diff; AlphaK/BattVolt same = skip; CabinTemp variant-only = added
        Assert.Equal(2, delta.Entries.Count);
        Assert.Equal("CabinTemp", delta.Entries[0].Name);
        Assert.Equal(22.5, delta.Entries[0].Physical);
        Assert.Equal("TorqueMax", delta.Entries[1].Name);
        Assert.Equal(150.0, delta.Entries[1].Physical);
    }

    [Fact]
    public void Extract_empty_delta_when_no_differences()
    {
        var delta = CalibrationVariantDelta.Extract("same", Baseline(), Baseline(at: FixedAt.AddHours(1)), "test");
        Assert.Empty(delta.Entries);
    }

    [Fact]
    public void Extract_carries_variant_name_and_a2l_sha()
    {
        var delta = Extract("winter");
        Assert.Equal("winter", delta.VariantName);
        Assert.Equal(Sha, delta.A2lSha256);
        Assert.Equal(2, delta.SchemaVersion);
    }


    // --- ContentFingerprint ---

    [Fact]
    public void ContentFingerprint_is_deterministic()
    {
        var f1 = CalibrationVariantDelta.ContentFingerprint(Baseline().Entries);
        var f2 = CalibrationVariantDelta.ContentFingerprint(Baseline().Entries);
        Assert.Equal(f1, f2);
        Assert.Equal(64, f1.Length); // SHA256 hex
    }

    [Fact]
    public void ContentFingerprint_ignores_timestamp_and_source()
    {
        var f1 = CalibrationVariantDelta.ContentFingerprint(Baseline(FixedAt).Entries);
        var f2 = CalibrationVariantDelta.ContentFingerprint(Baseline(FixedAt.AddDays(1)).Entries);
        Assert.Equal(f1, f2);
    }

    [Fact]
    public void ContentFingerprint_differs_for_different_values()
    {
        var f1 = CalibrationVariantDelta.ContentFingerprint(Baseline().Entries);
        var f2 = CalibrationVariantDelta.ContentFingerprint(Variant().Entries);
        Assert.NotEqual(f1, f2);
    }

    // --- ToJson diff stability + format ---

    [Fact]
    public void ToJson_is_byte_stable()
    {
        var j1 = Extract().ToJson();
        var j2 = Extract().ToJson();
        Assert.Equal(j1, j2);
    }

    [Fact]
    public void ToJson_format_is_pinned()
    {
        var json = Extract().ToJson();
        var expected = string.Join("\n",
            "{",
            "  \"schemaVersion\": 2,",
            "  \"variantName\": \"sport\",",
            "  \"baselineFingerprint\": \"" + CalibrationVariantDelta.ContentFingerprint(Baseline().Entries) + "\",",
            "  \"a2lSha256\": \"" + Sha + "\",",
            "  \"exportedAt\": \"2026-09-27T02:00:00.000Z\",",
            "  \"source\": \"test\",",
            "  \"calibrations\": [",
            "    {\"name\": \"CabinTemp\", \"physical\": 22.5, \"unit\": \"\\u00B0C\"},",
            "    {\"name\": \"TorqueMax\", \"physical\": 150, \"unit\": \"Nm\", \"raw\": \"4316\"}",
            "  ]",
            "}",
            "");
        Assert.Equal(expected, json);
    }

    // --- Parse ---

    [Fact]
    public void Parse_roundtrip_preserves_fields()
    {
        var json = Extract().ToJson();
        var parsed = CalibrationVariantDelta.Parse(json);
        Assert.Equal(2, parsed.SchemaVersion);
        Assert.Equal("sport", parsed.VariantName);
        Assert.Equal(Sha, parsed.A2lSha256);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("CabinTemp", parsed.Entries[0].Name);
        Assert.Equal(150.0, parsed.Entries[1].Physical);
    }

    [Fact]
    public void Parse_rejects_wrong_schema_version()
    {
        var json = Extract().ToJson().Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1");
        Assert.Throws<InvalidOperationException>(() => CalibrationVariantDelta.Parse(json));
    }

    [Fact]
    public void Parse_rejects_invalid_json()
    {
        Assert.Throws<InvalidOperationException>(() => CalibrationVariantDelta.Parse("not json"));
    }

    // --- EnsureBaselineMatches ---

    [Fact]
    public void EnsureBaselineMatches_accepts_correct_baseline()
    {
        var delta = Extract();
        delta.EnsureBaselineMatches(Baseline());
    }

    [Fact]
    public void EnsureBaselineMatches_rejects_different_baseline()
    {
        var delta = Extract();
        var otherBaseline = CalibrationParameterSet.Export(
            [new("TorqueMax", 999.0, "Nm", null)], Sha, "other");
        Assert.Throws<InvalidOperationException>(() => delta.EnsureBaselineMatches(otherBaseline));
    }
}
