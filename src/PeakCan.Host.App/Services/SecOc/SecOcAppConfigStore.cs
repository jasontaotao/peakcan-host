using System.IO;
using System.Text.Json;
using PeakCan.Host.Infrastructure.Channel.SecOc;

namespace PeakCan.Host.App.Services.SecOc;

/// <summary>
/// App 级 SecOC 配置读写（AppShell 连接路径使用）。Schema 与 CLI --secoc-config
/// 完全一致（SecOcPduEntry 数组），写入 camelCase + indented，读取
/// case-insensitive + 容忍注释。密钥只存 keyId 引用，本体在 DPAPI KeyStore。
/// </summary>
public static class SecOcAppConfigStore
{
    /// <summary>App 固定配置路径（%LocalAppData%\PeakCanHost\secoc-pdus.secoc）。</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCanHost", "secoc-pdus.secoc");

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>读取配置；文件缺失返回空列表（= 无保护，零回归语义）。</summary>
    public static IReadOnlyList<SecOcConfigLoader.SecOcPduEntry> Load(string? path = null)
    {
        var fullPath = path ?? DefaultConfigPath;
        if (!File.Exists(fullPath))
            return Array.Empty<SecOcConfigLoader.SecOcPduEntry>();

        var entries = JsonSerializer.Deserialize<List<SecOcConfigLoader.SecOcPduEntry>>(
            File.ReadAllText(fullPath), s_readOptions);
        return entries ?? new List<SecOcConfigLoader.SecOcPduEntry>();
    }

    /// <summary>写入配置；父目录不存在时创建。JSON 序列化错误原样上抛（fail-loud）。</summary>
    public static void Save(IReadOnlyList<SecOcConfigLoader.SecOcPduEntry> entries, string? path = null)
    {
        var fullPath = path ?? DefaultConfigPath;
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath,
            JsonSerializer.Serialize(entries.ToList(), s_writeOptions));
    }
}