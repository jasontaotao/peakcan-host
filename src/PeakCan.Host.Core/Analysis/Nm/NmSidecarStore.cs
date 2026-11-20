using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// <c>*.nmprofile.json</c> 读写。文件与 DBC 同目录同名；写入用临时文件 + 原子替换。
/// </summary>
public static class NmSidecarStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // spec §6.6.6 的 sidecar 用字符串枚举（"low_byte_source_address"）；
        // 兼容旧文件里的数字形式，避免历史数据整份被丢弃。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: true) },
    };

    public static string PathFor(string dbcPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbcPath) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(dbcPath);
        return System.IO.Path.Combine(dir, name + ".nmprofile.json");
    }

    public static NmUserSupplement? Load(string dbcPath) => LoadFrom(PathFor(dbcPath));

    /// <summary>从任意路径读取 sidecar；坏 JSON 视为不可解析（返回 null），IO 异常向上抛。</summary>
    public static NmUserSupplement? LoadFrom(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<NmUserSupplement>(File.ReadAllText(path), s_json);
        }
        catch (JsonException)
        {
            // 坏 sidecar 不阻断分析：当作没有补充输入。IO 异常不吞，继续向上抛。
            return null;
        }
    }

    public static void Save(string dbcPath, NmUserSupplement supplement)
        => SaveTo(PathFor(dbcPath), supplement);

    /// <summary>写到任意路径（导出配置用）；原子替换，已有文件的版本/创建时间不刷新。</summary>
    public static void SaveTo(string path, NmUserSupplement supplement)
    {
        // 已有 sidecar 的 created_at 是首次创建时间，重写时不得刷新；
        // version/profile_version 同样只能升不能降（向导重建 supplement 时不会透传它们）。
        // 新文件才用当前时间/默认版本。坏文件产生的 JsonException 按"无历史值"处理。
        var existing = TryReadExisting(path);
        var createdAt = existing?.CreatedAt ?? supplement.CreatedAt ?? DateTimeOffset.UtcNow;
        var version = Math.Max(1, Math.Max(supplement.Version, existing?.Version ?? 0));
        var profileVersion = Math.Max(
            1, Math.Max(supplement.ProfileVersion, existing?.ProfileVersion ?? 0));
        var toWrite = supplement with
        {
            Version = version,
            CreatedAt = createdAt,
            ProfileVersion = profileVersion,
        };

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(toWrite, s_json));
        File.Move(tmp, path, overwrite: true);
    }

    private static NmUserSupplement? TryReadExisting(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<NmUserSupplement>(File.ReadAllText(path), s_json);
        }
        catch (JsonException)
        {
            // 坏 sidecar 不阻断重写：当作没有历史值。IO 异常不吞，继续向上抛。
            return null;
        }
    }
}
