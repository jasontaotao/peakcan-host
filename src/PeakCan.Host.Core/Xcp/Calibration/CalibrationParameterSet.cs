using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Xcp.Calibration;

/// <summary>参数集单条目（D2：物理值为主存储；raw 仅导出时原始字节核对参考）。</summary>
public sealed record CalibrationEntry(string Name, double Physical, string? Unit, string? RawHex);

/// <summary>
/// S5-T1 标定参数集（spec D2）：明文可 diff 的 JSON 格式——每对象一行、对象名排序、
/// 两次导出字节一致；UTF-8 无 BOM、LF、2 空格缩进；物理值下发时经包侧 Encode 现算。
/// 指纹绑定：文件携带 A2L SHA256（与 S4 快照同源，<c>ContractSnapshot</c> 的 Sha256OfText），
/// 下发前 <see cref="EnsureMatches"/> 比对，不符拒绝。
/// </summary>
public sealed class CalibrationParameterSet
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; }
    public string A2lSha256 { get; }
    public DateTimeOffset ExportedAtUtc { get; }
    public string Source { get; }
    public IReadOnlyList<CalibrationEntry> Entries { get; }

    private CalibrationParameterSet(
        int schemaVersion, string a2lSha256, DateTimeOffset exportedAtUtc, string source,
        IReadOnlyList<CalibrationEntry> entries)
    {
        SchemaVersion = schemaVersion;
        A2lSha256 = a2lSha256;
        ExportedAtUtc = exportedAtUtc;
        Source = source;
        Entries = entries;
    }

    /// <summary>导出：对象名 Ordinal 排序（diff 稳定）；非有限物理值拒绝（JSON 无 NaN/Inf 表示）。</summary>
    public static CalibrationParameterSet Export(
        IReadOnlyList<CalibrationEntry> entries, string a2lSha256, string source, DateTimeOffset? exportedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(a2lSha256);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name))
                throw new ArgumentException("参数集条目对象名不能为空", nameof(entries));
            if (!double.IsFinite(e.Physical))
                throw new ArgumentException($"对象 '{e.Name}' 物理值非有限（{e.Physical}），JSON 无法表示", nameof(entries));
        }

        var sorted = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        return new(CurrentSchemaVersion, a2lSha256, (exportedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), source, sorted);
    }

    /// <summary>序列化：手工拼装保证字节级确定（diff 稳定），字符串经 JsonSerializer 转义。</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"schemaVersion\": ").Append(CurrentSchemaVersion).Append(",\n");
        sb.Append("  \"a2lSha256\": ").Append(Json(A2lSha256)).Append(",\n");
        sb.Append("  \"exportedAt\": \"").Append(FormatTimestamp(ExportedAtUtc)).Append("\",\n");
        sb.Append("  \"source\": ").Append(Json(Source)).Append(",\n");
        sb.Append("  \"calibrations\": [\n");
        for (var i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            sb.Append("    {\"name\": ").Append(Json(e.Name));
            sb.Append(", \"physical\": ").Append(e.Physical.ToString("R", CultureInfo.InvariantCulture));
            if (e.Unit is not null)
                sb.Append(", \"unit\": ").Append(Json(e.Unit));
            if (e.RawHex is not null)
                sb.Append(", \"raw\": ").Append(Json(e.RawHex));
            sb.Append('}');
            if (i < Entries.Count - 1)
                sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]\n}");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>UTF-8 无 BOM 字节（文件落盘/比较面）。</summary>
    public byte[] ToJsonBytes() => Encoding.UTF8.GetBytes(ToJson());

    /// <summary>解析：schema 版本/指纹必检；条目重排序保 diff 稳定；非法 JSON/版本/指纹 → InvalidOperationException。</summary>
    public static CalibrationParameterSet Parse(string json)
    {
        ParameterSetDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ParameterSetDto>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("参数集 JSON 无效：" + ex.Message, ex);
        }
        if (dto is null)
            throw new InvalidOperationException("参数集 JSON 为空");
        if (dto.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"参数集 schemaVersion 不支持：{dto.SchemaVersion}（当前 {CurrentSchemaVersion}）");
        if (string.IsNullOrWhiteSpace(dto.A2lSha256))
            throw new InvalidOperationException("参数集缺 A2L SHA256 指纹");

        var entries = (dto.Calibrations ?? [])
            .Select(e => new CalibrationEntry(
                e.Name ?? throw new InvalidOperationException("参数集条目缺对象名"),
                e.Physical,
                e.Unit,
                e.Raw))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();

        var exportedAt = string.IsNullOrEmpty(dto.ExportedAt)
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.Parse(dto.ExportedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        return new(dto.SchemaVersion, dto.A2lSha256, exportedAt, dto.Source ?? string.Empty, entries);
    }

    /// <summary>指纹绑定（D2）：与当前 A2L SHA256 比对，不符拒绝下发。</summary>
    public void EnsureMatches(string currentA2lSha256)
    {
        if (!string.Equals(A2lSha256, currentA2lSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"参数集指纹不符：文件 [{Short(A2lSha256)}] ≠ 当前 A2L [{Short(currentA2lSha256)}]（拒绝下发）");
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string FormatTimestamp(DateTimeOffset utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8] + "…";

    private sealed record ParameterSetDto(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
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
