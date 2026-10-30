using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Ui;

/// <summary>脚本页「最近打开」的一条记录。</summary>
public sealed record RecentScriptDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("openedAtUtcTicks")] long OpenedAtUtcTicks);

/// <summary>
/// 脚本文件的最近打开列表（<c>%APPDATA%/PeakCan.Host/recent-scripts.json</c>）。
/// <para>
/// 为什么不复用 <c>RecentSessionsService</c>：那份 MRU 直接绑在「文件 → 最近打开」菜单上，
/// 点一条就按**会话**去打开；把脚本路径混进去会让菜单点出错误的行为。会话与脚本是两类
/// 目标，各自的 MRU 分开存。
/// </para>
/// <para>
/// 文件契约照 <see cref="LayoutStateStore"/>：schema 信封、tmp+rename 原子写、
/// 损坏/超大容错。每次 <see cref="Add"/> 都是"读—改—写"，条目少（≤8）代价可忽略。
/// </para>
/// </summary>
public sealed partial class RecentScriptsStore
{
    private const string CurrentSchema = "recent-scripts/v1";

    /// <summary>最多保留多少条。再多就是噪声，用户也不会往下翻。</summary>
    public const int MaxEntries = 8;

    /// <summary>文件体积兜底（正常只有几 KB）。</summary>
    public const long MaxLoadFileBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<RecentScriptsStore> _logger;

    public RecentScriptsStore(ILogger<RecentScriptsStore> logger)
        : this(logger, null) { }

    public RecentScriptsStore(ILogger<RecentScriptsStore> logger, string? overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    /// <summary>存储文件路径（测试与诊断用）。</summary>
    public string FilePath => _path;

    /// <summary>读取列表（最新在前）。缺失 / 损坏 / 超大一律返回空列表，不抛。</summary>
    public IReadOnlyList<RecentScriptDto> Load()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return Array.Empty<RecentScriptDto>();
            if (info.Length > MaxLoadFileBytes)
            {
                LogOversized(_logger, _path, info.Length, MaxLoadFileBytes);
                return Array.Empty<RecentScriptDto>();
            }

            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(_path), JsonOpts);
            var entries = envelope?.Entries;
            if (entries is null) return Array.Empty<RecentScriptDto>();

            // 手工改坏的配置不该让菜单出现空标题项。
            // OfType 同时过滤 JSON 里的 null 元素：`{"entries":[null]}` 反序列化
            // 会给出一个 null 项（非空标注在运行时不生效），下面解引用 e.Path 会 NRE，
            // 而这条路径跑在启动流程里 —— 会变成"每次开机都崩且文件不变，只能手删"。
            return entries
                .OfType<RecentScriptDto>()
                .Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            LogCorrupt(_logger, _path, ex);
            return Array.Empty<RecentScriptDto>();
        }
    }

    /// <summary>
    /// 把 <paramref name="path"/> 置顶并返回更新后的列表（最新在前）。
    /// 路径去重按大小写不敏感（Windows 语义），空路径直接返回现有列表。
    /// </summary>
    public IReadOnlyList<RecentScriptDto> Add(string path)
    {
        var current = Load();
        if (string.IsNullOrWhiteSpace(path)) return current;

        var updated = new List<RecentScriptDto>(MaxEntries)
        {
            new(path, DateTime.UtcNow.Ticks),
        };
        updated.AddRange(current.Where(
            e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)));
        if (updated.Count > MaxEntries) updated.RemoveRange(MaxEntries, updated.Count - MaxEntries);

        Persist(updated);
        return updated;
    }

    /// <summary>清空列表。删除失败返回 false（调用方决定要不要提示）。</summary>
    public bool Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            LogClearFailed(_logger, ex, _path);
            return false;
        }
    }

    private void Persist(IReadOnlyList<RecentScriptDto> entries)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(new Envelope { Entries = entries.ToList() }, JsonOpts);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            LogSaveFailed(_logger, ex, _path);
            return;
        }

        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
        }
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakCan.Host", "recent-scripts.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "Recent-scripts file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recent-scripts file exceeds size cap ({Actual} > {Cap} bytes), ignoring: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recent-scripts save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recent-scripts delete failed: {Path}")]
    private static partial void LogClearFailed(ILogger logger, Exception ex, string path);

    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("entries")] public List<RecentScriptDto>? Entries { get; set; }
    }
}
