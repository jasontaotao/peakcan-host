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
    };

    public static string PathFor(string dbcPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbcPath) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(dbcPath);
        return System.IO.Path.Combine(dir, name + ".nmprofile.json");
    }

    public static NmUserSupplement? Load(string dbcPath)
    {
        var path = PathFor(dbcPath);
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
    {
        var path = PathFor(dbcPath);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(supplement, s_json));
        File.Move(tmp, path, overwrite: true);
    }
}
