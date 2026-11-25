using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Analysis.Nm;

public enum NmReasonKind
{
    Raw,
    Enum,
    Bitmask,
}

public sealed record NmReasonSource(string? Doc, string? Revision);

public sealed record NmReasonFieldDef(
    string Signal,
    string CanIdHex,
    bool IsExtended,
    NmReasonKind Kind = NmReasonKind.Raw,
    IReadOnlyDictionary<long, string>? Entries = null);

public sealed record NmReasonDictionary(
    int Version,
    IReadOnlyList<NmReasonFieldDef> Fields,
    NmReasonSource? Source = null);

/// <summary>加载成功后的字典及其来源身份；报告用 Path/Sha256 追溯解释来源。</summary>
public sealed record LoadedNmReasonDictionary(
    NmReasonDictionary Dictionary,
    string Path,
    string Sha256);

public sealed class NmReasonDictionaryException : Exception
{
    public NmReasonDictionaryException(string message) : base(message) { }
    public NmReasonDictionaryException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// <c>*.nmreasons.json</c> 读写。第一版只要求字段声明；条目与 kind 可选。
/// </summary>
public static class NmReasonDictionaryStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        // 只接受字符串 kind（"raw"/"enum"/"bitmask"）；数字形式会绕过未知 kind 校验。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>相对路径相对 nmprofile 所在目录解析；绝对路径原样返回。</summary>
    public static string ResolvePath(string profilePath, string reasonsPath)
    {
        if (System.IO.Path.IsPathRooted(reasonsPath)) return reasonsPath;
        var dir = System.IO.Path.GetDirectoryName(profilePath) ?? string.Empty;
        return System.IO.Path.Combine(dir, reasonsPath);
    }

    public static LoadedNmReasonDictionary LoadFrom(string path)
    {
        if (!File.Exists(path))
            throw new NmReasonDictionaryException($"原因字典不存在：{path}");
        try
        {
            var bytes = File.ReadAllBytes(path);
            string json;
            try
            {
                // 严格 UTF-8：ANSI/GBK 保存的字典会静默乱码，必须明确报错。
                json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                // 记事本/PowerShell 常写出带 BOM 的 UTF-8；BOM 不是 JSON 的一部分。
                // 哈希仍用原始 bytes，保留含 BOM 的文件身份。
                if (json.Length > 0 && json[0] == (char)0xFEFF) json = json[1..];
            }
            catch (DecoderFallbackException ex)
            {
                throw new NmReasonDictionaryException($"原因字典不是有效 UTF-8：{path}", ex);
            }
            return Load(json, path, bytes);
        }
        catch (IOException ex)
        {
            throw new NmReasonDictionaryException($"原因字典读取失败：{path}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new NmReasonDictionaryException($"原因字典无读取权限：{path}", ex);
        }
    }

    public static LoadedNmReasonDictionary Load(string json, string path)
        => Load(json, path, utf8Bytes: null);

    private static LoadedNmReasonDictionary Load(string json, string path, byte[]? utf8Bytes)
    {
        NmReasonDictionaryDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<NmReasonDictionaryDto>(json, s_json);
        }
        catch (JsonException ex)
        {
            throw new NmReasonDictionaryException($"原因字典 JSON 解析失败：{ex.Message}", ex);
        }

        if (dto is null)
            throw new NmReasonDictionaryException("原因字典反序列化结果为空");
        // 第一版只认识 version 1；未来版本按当前语义解释会静默变义，必须明确拒绝。
        if (dto.Version != 1)
            throw new NmReasonDictionaryException(
                $"不支持的原因字典 version：{dto.Version}（本版只支持 1）");
        if (dto.Fields is not { Count: > 0 })
            throw new NmReasonDictionaryException(
                "原因字典 fields 为空：必须至少声明一个要观察的字段");

        var fields = new List<NmReasonFieldDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dto.Fields ?? Array.Empty<ReasonFieldDto>())
        {
            if (item is null)
                throw new NmReasonDictionaryException("fields 含空项");
            if (string.IsNullOrWhiteSpace(item.Signal))
                throw new NmReasonDictionaryException("fields[].signal 必填");
            if (string.IsNullOrWhiteSpace(item.CanIdHex))
                throw new NmReasonDictionaryException("fields[].can_id 必填");
            if (item.Extended is null)
                throw new NmReasonDictionaryException(
                    $"字段 {item.Signal} 缺少 extended：标准帧/扩展帧必须显式声明");
            // 规范化成唯一的 0x 大写形式：0x1 / 0x01 / 0x018ffaf4 都落成同一文本，
            // 否则下游按字符串比较帧时会静默漏掉整条时间线。
            var canIdHex = NormalizeCanId(item.CanIdHex, item.Signal);

            var kind = item.Kind ?? NmReasonKind.Raw;
            if (!Enum.IsDefined(kind))
                throw new NmReasonDictionaryException(
                    $"字段 {item.Signal} 的 kind 非法：{(int)kind}");
            var entries = item.Entries is { Count: > 0 }
                ? new Dictionary<long, string>(item.Entries)
                : null;

            if (entries is not null)
            {
                foreach (var pair in entries)
                {
                    // 原始值是无符号位模式，负键永远不可能命中：加载期拒绝，不静默留着。
                    if (pair.Key < 0)
                        throw new NmReasonDictionaryException(
                            $"字段 {item.Signal} 的条目键 {pair.Key} 不能为负");
                    // 空白文本会显示成空标签，比“含义未配置”更糟：无法区分没配还是配坏。
                    if (string.IsNullOrWhiteSpace(pair.Value))
                        throw new NmReasonDictionaryException(
                            $"字段 {item.Signal} 的条目 {pair.Key} 文本为空");
                }
                if (kind == NmReasonKind.Bitmask)
                {
                    foreach (var key in entries.Keys)
                    {
                        if (key <= 0 || (key & (key - 1)) != 0)
                            throw new NmReasonDictionaryException(
                                $"字段 {item.Signal} 的 bitmask 条目键 {key} 不是 2 的幂");
                    }
                }
            }

            var identity = $"{canIdHex}|{item.Extended}|{item.Signal}";
            if (!seen.Add(identity))
                throw new NmReasonDictionaryException($"字段重复声明：{identity}");

            fields.Add(new NmReasonFieldDef(
                item.Signal, canIdHex, item.Extended.Value, kind, entries));
        }

        var dictionary = new NmReasonDictionary(
            dto.Version, fields,
            dto.Source is null ? null : new NmReasonSource(dto.Source.Doc, dto.Source.Revision));

        // 文件加载时按原始字节哈希（Path/Hash 一起构成来源身份）；字符串入口按 UTF-8 编码。
        var hash = Convert.ToHexString(
            SHA256.HashData(utf8Bytes ?? Encoding.UTF8.GetBytes(json)));
        return new LoadedNmReasonDictionary(dictionary, path, hash);
    }

    private static string NormalizeCanId(string text, string signal)
    {
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var id))
        {
            throw new NmReasonDictionaryException(
                $"字段 {signal} 的 can_id「{text}」不是 0x 十六进制");
        }
        return "0x" + id.ToString("X", CultureInfo.InvariantCulture);
    }

    private sealed record NmReasonDictionaryDto(
        int Version,
        IReadOnlyList<ReasonFieldDto>? Fields,
        SourceDto? Source);

    private sealed record ReasonFieldDto(
        string? Signal,
        // SnakeCaseLower 会把 CanIdHex 命名为 can_id_hex；契约字段是 can_id。
        [property: JsonPropertyName("can_id")] string? CanIdHex,
        bool? Extended,
        NmReasonKind? Kind,
        Dictionary<long, string>? Entries);

    private sealed record SourceDto(string? Doc, string? Revision);
}

/// <summary>
/// 缓存键里的原因来源身份。三种状态必须可区分：
/// <list type="bullet">
/// <item>未配置字典 → <c>none</c>；</item>
/// <item>配置了但加载失败 → <c>unavailable:说明</c>（说明含路径，换字典/删配置都会失效）；</item>
/// <item>加载成功 → <c>内容哈希:路径</c>（内容或路径变化都不能复用旧报告）。</item>
/// </list>
/// 失败态与未配置态若都压成 "none"，用户修好/删掉 reasons_path 后会命中旧报告，
/// 看到过期的“字典不可用/未配置”提示。
/// </summary>
public static class NmReasonCacheToken
{
    public static string For(LoadedNmReasonDictionary? dictionary, string? loadNote)
    {
        if (dictionary is not null) return dictionary.Sha256 + ":" + dictionary.Path;
        return loadNote is null ? "none" : "unavailable:" + loadNote;
    }
}
