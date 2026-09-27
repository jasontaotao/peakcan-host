using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Xcp.Calibration;

/// <summary>
/// S8 variant delta (spec D1/D2/D3): standalone JSON schemaVersion=2.
/// Delta = only entries differing from baseline (D3: extract + direct apply via S5 reconciler).
/// Baseline fingerprint = content fingerprint of baseline entries (D2: exportedAt/source independent).
/// Byte-deterministic serialization (same style as S5 CalibrationParameterSet).
/// </summary>
public sealed class CalibrationVariantDelta
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; }
    public string VariantName { get; }
    public string BaselineFingerprint { get; }
    public string A2lSha256 { get; }
    public DateTimeOffset ExportedAtUtc { get; }
    public string Source { get; }
    public IReadOnlyList<CalibrationEntry> Entries { get; }

    private CalibrationVariantDelta(
        int schemaVersion, string variantName, string baselineFingerprint, string a2lSha256,
        DateTimeOffset exportedAtUtc, string source, IReadOnlyList<CalibrationEntry> entries)
    {
        SchemaVersion = schemaVersion;
        VariantName = variantName;
        BaselineFingerprint = baselineFingerprint;
        A2lSha256 = a2lSha256;
        ExportedAtUtc = exportedAtUtc;
        Source = source;
        Entries = entries;
    }

    /// <summary>
    /// Extract: baseline + variant full parameter sets → delta (only differing entries, Ordinal sorted).
    /// Variant-only entries are "added" (included); baseline-only entries are "removed" (not in delta —
    /// absence from delta means "don't touch" when applied via S5 reconciler).
    /// </summary>
    public static CalibrationVariantDelta Extract(
        string variantName,
        CalibrationParameterSet baseline,
        CalibrationParameterSet variant,
        string source,
        DateTimeOffset? exportedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantName);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(source);

        var baselineMap = baseline.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var diffEntries = new List<CalibrationEntry>();
        foreach (var v in variant.Entries)
        {
            if (!double.IsFinite(v.Physical))
                throw new ArgumentException($"对象 '{v.Name}' 物理值非有限（{v.Physical}），JSON 无法表示", nameof(variant));
            if (baselineMap.TryGetValue(v.Name, out var b)
                && b.Physical.Equals(v.Physical)
                && string.Equals(b.Unit, v.Unit, StringComparison.Ordinal))
            {
                continue; // same value + unit → no difference
            }
            diffEntries.Add(v);
        }

        diffEntries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var fingerprint = ContentFingerprint(baseline.Entries);
        return new(CurrentSchemaVersion, variantName, fingerprint, baseline.A2lSha256,
            (exportedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), source, diffEntries);
    }

    /// <summary>
    /// Content fingerprint (D2): sorted entries → deterministic serialization → SHA256 hex.
    /// exportedAt/source independent — same entries always produce the same fingerprint.
    /// </summary>
    public static string ContentFingerprint(IReadOnlyList<CalibrationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sorted = entries.OrderBy(e => e.Name, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var e in sorted)
        {
            sb.Append("{\"name\": ").Append(JsonSerializer.Serialize(e.Name));
            sb.Append(", \"physical\": ").Append(e.Physical.ToString("R", CultureInfo.InvariantCulture));
            if (e.Unit is not null)
                sb.Append(", \"unit\": ").Append(JsonSerializer.Serialize(e.Unit));
            if (e.RawHex is not null)
                sb.Append(", \"raw\": ").Append(JsonSerializer.Serialize(e.RawHex));
            sb.Append("}\n");
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Byte-deterministic serialization (same style as S5: 2-space indent, LF, UTF-8 no BOM).</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"schemaVersion\": ").Append(CurrentSchemaVersion).Append(",\n");
        sb.Append("  \"variantName\": ").Append(JsonSerializer.Serialize(VariantName)).Append(",\n");
        sb.Append("  \"baselineFingerprint\": ").Append(JsonSerializer.Serialize(BaselineFingerprint)).Append(",\n");
        sb.Append("  \"a2lSha256\": ").Append(JsonSerializer.Serialize(A2lSha256)).Append(",\n");
        sb.Append("  \"exportedAt\": \"").Append(FormatTimestamp(ExportedAtUtc)).Append("\",\n");
        sb.Append("  \"source\": ").Append(JsonSerializer.Serialize(Source)).Append(",\n");
        sb.Append("  \"calibrations\": [\n");
        for (var i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            sb.Append("    {\"name\": ").Append(JsonSerializer.Serialize(e.Name));
            sb.Append(", \"physical\": ").Append(e.Physical.ToString("R", CultureInfo.InvariantCulture));
            if (e.Unit is not null)
                sb.Append(", \"unit\": ").Append(JsonSerializer.Serialize(e.Unit));
            if (e.RawHex is not null)
                sb.Append(", \"raw\": ").Append(JsonSerializer.Serialize(e.RawHex));
            sb.Append('}');
            if (i < Entries.Count - 1)
                sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]\n}");
        sb.Append('\n');
        return sb.ToString();
    }

    public byte[] ToJsonBytes() => Encoding.UTF8.GetBytes(ToJson());

    /// <summary>Parse: schema version + variantName + baselineFingerprint + a2lSha256 all required.</summary>
    public static CalibrationVariantDelta Parse(string json)
    {
        DeltaDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DeltaDto>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("变体 delta JSON 无效：" + ex.Message, ex);
        }
        if (dto is null)
            throw new InvalidOperationException("变体 delta JSON 为空");
        if (dto.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"变体 delta schemaVersion 不支持：{dto.SchemaVersion}（当前 {CurrentSchemaVersion}）");
        if (string.IsNullOrWhiteSpace(dto.VariantName))
            throw new InvalidOperationException("变体 delta 缺 variantName");
        if (string.IsNullOrWhiteSpace(dto.BaselineFingerprint))
            throw new InvalidOperationException("变体 delta 缺 baselineFingerprint");
        if (string.IsNullOrWhiteSpace(dto.A2lSha256))
            throw new InvalidOperationException("变体 delta 缺 A2L SHA256 指纹");

        var entries = (dto.Calibrations ?? [])
            .Select(e => new CalibrationEntry(
                e.Name ?? throw new InvalidOperationException("变体 delta 条目缺对象名"),
                e.Physical,
                e.Unit,
                e.Raw))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();

        DateTimeOffset exportedAt;
        if (string.IsNullOrEmpty(dto.ExportedAt))
        {
            exportedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            try
            {
                exportedAt = DateTimeOffset.Parse(dto.ExportedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("变体 delta exportedAt 时间戳无效：" + ex.Message, ex);
            }
        }

        return new(dto.SchemaVersion, dto.VariantName, dto.BaselineFingerprint, dto.A2lSha256,
            exportedAt, dto.Source ?? string.Empty, entries);
    }

    /// <summary>Baseline fingerprint check (D2): content fingerprint comparison with current baseline parameter set.</summary>
    public void EnsureBaselineMatches(CalibrationParameterSet currentBaseline)
    {
        ArgumentNullException.ThrowIfNull(currentBaseline);
        var current = ContentFingerprint(currentBaseline.Entries);
        if (!string.Equals(BaselineFingerprint, current, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"变体 delta 基线指纹不符：文件 [{Short(BaselineFingerprint)}] ≠ 当前基线 [{Short(current)}]（拒绝应用）");
    }

    /// <summary>A2L SHA256 check (same as S5 parameter set): structural anchor validation.</summary>
    public void EnsureA2lMatches(string currentA2lSha256)
    {
        if (!string.Equals(A2lSha256, currentA2lSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"变体 delta A2L 指纹不符：文件 [{Short(A2lSha256)}] ≠ 当前 A2L [{Short(currentA2lSha256)}]（拒绝应用）");
    }

    private static string FormatTimestamp(DateTimeOffset utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8] + "…";

    private sealed record DeltaDto(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("variantName")] string? VariantName,
        [property: JsonPropertyName("baselineFingerprint")] string? BaselineFingerprint,
        [property: JsonPropertyName("a2lSha256")] string? A2lSha256,
        [property: JsonPropertyName("exportedAt")] string? ExportedAt,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("calibrations")] List<EntryDto>? Calibrations);

    private sealed record EntryDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("physical")] double Physical,
        [property: JsonPropertyName("unit")] string? Unit,
        [property: JsonPropertyName("raw")] string? Raw);
}
