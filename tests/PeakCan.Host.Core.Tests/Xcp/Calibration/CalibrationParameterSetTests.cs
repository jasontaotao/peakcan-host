using System.Globalization;
using System.Text;
using PeakCan.Host.Core.Xcp.Calibration;

namespace PeakCan.Host.Core.Tests.Xcp.Calibration;

/// <summary>
/// S5-T1：参数集 codec（spec D2）——JSON 明文、每对象一行、对象名排序、
/// 两次导出字节一致（diff 稳定）、UTF-8 无 BOM、指纹校验。
/// </summary>
public sealed class CalibrationParameterSetTests
{
    private static readonly string Sha = "a" + new string('0', 63);
    private static readonly DateTimeOffset FixedAt =
        DateTimeOffset.Parse("2026-09-26T02:00:00Z", CultureInfo.InvariantCulture);
    private static readonly string[] SortedNames = ["AlphaK", "BattVolt", "TorqueMax"];

    private static List<CalibrationEntry> Entries() =>
    [
        new("TorqueMax", 123.5, "Nm", "42F6"),
        new("AlphaK", 0.001, null, null),
        new("BattVolt", 3.7, "V", null),
    ];

    private static CalibrationParameterSet Set(string? sha = null, DateTimeOffset? at = null) =>
        CalibrationParameterSet.Export(Entries(), sha ?? Sha, "test", at ?? FixedAt);

    [Fact]
    public void Export_then_parse_roundtrip_preserves_entries_and_fingerprint()
    {
        var json = Set().ToJson();
        var parsed = CalibrationParameterSet.Parse(json);

        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal(Sha, parsed.A2lSha256);
        Assert.Equal("test", parsed.Source);
        Assert.Equal(3, parsed.Entries.Count);
        Assert.Equal("AlphaK", parsed.Entries[0].Name);
        Assert.Equal(0.001, parsed.Entries[0].Physical);
        Assert.Null(parsed.Entries[0].Unit);
        Assert.Equal("TorqueMax", parsed.Entries[2].Name);
        Assert.Equal(123.5, parsed.Entries[2].Physical);
        Assert.Equal("Nm", parsed.Entries[2].Unit);
        Assert.Equal("42F6", parsed.Entries[2].RawHex);
    }

    [Fact]
    public void Export_is_byte_stable_and_sorted()
    {
        // diff 稳定（S1 四钉字面要求）：同输入两次导出字节一致（时间戳显式注入）；
        // 输入乱序 → 输出按对象名排序。
        Assert.Equal(Set().ToJson(), Set().ToJson());
        Assert.Equal(3, Set().Entries.Count);
        Assert.Equal(SortedNames, Set().Entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Format_is_pinned()
    {
        var expected = string.Join("\n",
            "{",
            "  \"schemaVersion\": 1,",
            "  \"a2lSha256\": \"" + Sha + "\",",
            "  \"exportedAt\": \"2026-09-26T02:00:00.000Z\",",
            "  \"source\": \"test\",",
            "  \"calibrations\": [",
            "    {\"name\": \"AlphaK\", \"physical\": 0.001},",
            "    {\"name\": \"BattVolt\", \"physical\": 3.7, \"unit\": \"V\"},",
            "    {\"name\": \"TorqueMax\", \"physical\": 123.5, \"unit\": \"Nm\", \"raw\": \"42F6\"}",
            "  ]",
            "}",
            "");
        Assert.Equal(expected, Set().ToJson());
    }

    [Fact]
    public void ToJsonBytes_is_utf8_without_bom()
    {
        var bytes = Set().ToJsonBytes();
        Assert.Equal((byte)'{', bytes[0]); // 无 BOM（0xEF 0xBB 0xBF 会打头）
        Assert.Equal(Encoding.UTF8.GetBytes(Set().ToJson()), bytes);
    }

    [Fact]
    public void Parse_rejects_wrong_schema_version_and_missing_fingerprint()
    {
        var badVersion = Set().ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        Assert.Throws<InvalidOperationException>(() => CalibrationParameterSet.Parse(badVersion));

        var noSha = System.Text.RegularExpressions.Regex.Replace(Set().ToJson(),
            "  \"a2lSha256\": [^,]+,\n", "");
        Assert.Throws<InvalidOperationException>(() => CalibrationParameterSet.Parse(noSha));
    }

    [Fact]
    public void Parse_rejects_malformed_json()
    {
        Assert.Throws<InvalidOperationException>(() => CalibrationParameterSet.Parse("{ not json"));
    }

    [Fact]
    public void EnsureMatches_throws_on_fingerprint_mismatch()
    {
        var set = Set();
        set.EnsureMatches(Sha); // 匹配不抛

        var wrong = "b" + new string('1', 63);
        var ex = Assert.Throws<InvalidOperationException>(() => set.EnsureMatches(wrong));
        Assert.Contains("指纹", ex.Message);
    }

    [Fact]
    public void Export_rejects_non_finite_physical()
    {
        // JSON 无 NaN/Inf 表示；非有限物理值在导出即拒（下发面 Encode 再拒一次）。
        Assert.Throws<ArgumentException>(() => CalibrationParameterSet.Export(
            [new CalibrationEntry("X", double.NaN, null, null)], Sha, "test"));
        Assert.Throws<ArgumentException>(() => CalibrationParameterSet.Export(
            [new CalibrationEntry("X", double.PositiveInfinity, null, null)], Sha, "test"));
    }
}
