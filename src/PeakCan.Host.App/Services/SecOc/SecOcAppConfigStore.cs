using System.Globalization;
using System.IO;
using System.Text.Json;
using PeakCan.Host.Infrastructure.Channel.SecOc;

namespace PeakCan.Host.App.Services.SecOc;

/// <summary>App 固定配置的三态状态（未启用 / 就绪 / 配置错误）。</summary>
public enum SecOcConfigStatusKind { NotConfigured, Ready, Error }

/// <summary>工具栏状态指示数据（PduCount 仅 Ready 时有效）。</summary>
public sealed record SecOcConfigStatus(SecOcConfigStatusKind Kind, int PduCount, string? Error);

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

    /// <summary>
    /// 连接路径 PDU 配置读取（AppHostBuilder provider 用）：按通道 handle 过滤——
    /// 文件缺失或该通道无归属 PDU 返回 null（= 该通道不启用 SecOC，零回归）；
    /// 存在则委托 CLI 同款 BuildFromEntries（keyId 缺失 / JSON 损坏 fail-loud）。
    /// 归属规则（2026-09-17 缺口 1a）：entry.Handle 空 = 全局兜底（所有通道适用）；
    /// 非空 = 仅匹配该 handle 的通道。旧配置（无 handle 字段）→ 全部兜底，向后兼容。
    /// </summary>
    public static IReadOnlyDictionary<uint, SecOcPduConfig>? LoadForConnectPath(
        ushort handle, string? path = null, string? storeDir = null)
    {
        var entries = Load(path);
        if (entries.Count == 0)
            return null;
        var scoped = entries
            .Where(e => string.IsNullOrWhiteSpace(e.Handle) || ParseHandle(e.Handle) == handle)
            .ToList();
        if (scoped.Count == 0)
            return null;
        return SecOcConfigLoader.BuildFromEntries(scoped, storeDir);
    }

    /// <summary>解析通道 Handle（hex "0x51" / dec "81"）；非法值抛（配置错误必须可见）。</summary>
    private static ushort ParseHandle(string raw)
    {
        var text = raw.Trim();
        var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var ok = ushort.TryParse(
            isHex ? text[2..] : text,
            isHex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value);
        if (!ok)
            throw new InvalidOperationException($"SecOC config: invalid channel Handle '{raw}'.");
        return value;
    }

    // 缺口 3（2026-09-17）：工具栏启用状态可见性。

    /// <summary>
    /// 全局配置状态评估（不过滤 handle——状态是 App 级指示）：文件缺失/空 →
    /// NotConfigured；可解析（含 keyId 校验）→ Ready(count)；JSON 损坏 / keyId 缺失 /
    /// 空 PDU 列表 → Error(message)。供 AppShell 工具栏状态指示用。
    /// </summary>
    public static SecOcConfigStatus GetStatus(string? path = null, string? storeDir = null)
    {
        try
        {
            var entries = Load(path);
            if (entries.Count == 0)
                return new(SecOcConfigStatusKind.NotConfigured, 0, null);
            var pdus = SecOcConfigLoader.BuildFromEntries(entries, storeDir);
            return new(SecOcConfigStatusKind.Ready, pdus.Count, null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return new(SecOcConfigStatusKind.Error, 0, ex.Message);
        }
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