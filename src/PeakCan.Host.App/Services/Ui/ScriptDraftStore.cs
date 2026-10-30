using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Ui;

/// <summary>
/// 脚本页的草稿快照。
/// <para><see cref="SavedAtUtcTicks"/> 是"写草稿的时刻"，用来在恢复时判断草稿是否已经过期：
/// 文件比草稿新（保存成功但清理草稿失败、或被外部编辑器改过）时不能拿旧草稿去覆盖它。</para>
/// </summary>
public sealed record ScriptDraftDto(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("savedAtUtcTicks")] long SavedAtUtcTicks);

/// <summary>
/// 脚本页草稿持久化（<c>%APPDATA%/PeakCan.Host/script-draft.json</c>）。
/// 镜像 <see cref="LayoutStateStore"/> 的文件契约：schema 信封、原子 tmp+rename、
/// 损坏/超大容错。区别是**写入也要卡上限**——脚本是任意文本，只卡读取会让超大草稿
/// "写得进、读不回"，下次启动静默消失。
/// </summary>
public sealed partial class ScriptDraftStore
{
    private const string CurrentSchema = "script-draft/v1";

    /// <summary>
    /// 草稿文本的硬上限。编辑器把全文当 JS 字面量嵌进 HTML 再走
    /// <c>NavigateToString</c>（WebView2 对内容有约 2 MB 上限，中文按 JSON 转义约 6 倍），
    /// 200K 字符留足余量。
    /// </summary>
    public const int MaxDraftChars = 200_000;

    /// <summary>读取侧兜底：字符上限之外再卡一道文件体积，防手工膨胀的 JSON。</summary>
    public const long MaxLoadFileBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly ILogger<ScriptDraftStore> _logger;

    public ScriptDraftStore(ILogger<ScriptDraftStore> logger)
        : this(logger, null) { }

    public ScriptDraftStore(ILogger<ScriptDraftStore> logger, string? overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    /// <summary>草稿文件路径（测试与诊断用）。</summary>
    public string FilePath => _path;

    /// <summary>读取草稿；缺失 / 损坏 / 超大 / 空文本一律返回 null，不抛。</summary>
    public ScriptDraftDto? Load()
    {
        // 整段都在 try 里：文件可能在 Exists 与 Length 之间被另一个实例删掉
        // （两个进程共享同一个草稿路径），而读取发生在启动路径上——异常逃出去
        // 就是"每次开机都崩，且草稿还在所以下次继续崩"。
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return null;

            if (info.Length > MaxLoadFileBytes)
            {
                LogOversized(_logger, _path, info.Length, MaxLoadFileBytes);
                return null;
            }

            var json = File.ReadAllText(_path);
            var draft = JsonSerializer.Deserialize<Envelope>(json, JsonOpts)?.Draft;
            if (draft is null || string.IsNullOrEmpty(draft.Text)) return null;

            if (draft.Text.Length > MaxDraftChars)
            {
                // 读回来也用不了：编辑器装不下，恢复只会制造半个文件。
                LogTooLarge(_logger, _path, draft.Text.Length, MaxDraftChars);
                return null;
            }
            return draft;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            LogCorrupt(_logger, _path, ex);
            return null;
        }
    }

    /// <summary>写入草稿。超限 / IO 失败返回 false（调用方要能看见，不能静默）。</summary>
    public bool Save(ScriptDraftDto draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Text.Length > MaxDraftChars)
        {
            LogTooLarge(_logger, _path, draft.Text.Length, MaxDraftChars);
            return false;
        }

        var envelope = new Envelope { Draft = draft };
        string json;
        try
        {
            json = JsonSerializer.Serialize(envelope, JsonOpts);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            // 序列化逃出 try 会变成 DispatcherTimer 回调里的未处理异常（直接崩进程）。
            LogSaveFailed(_logger, ex, _path);
            return false;
        }

        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
            return false;
        }
    }

    /// <summary>删除草稿。文件不存在视为成功；删除失败返回 false。</summary>
    public bool Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogClearFailed(_logger, ex, _path);
            return false;
        }
    }

    private static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakCan.Host", "script-draft.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "Script draft corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script draft exceeds size cap ({Actual} > {Cap} bytes), ignoring: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script draft too large ({Actual} > {Cap} chars), skipped: {Path}")]
    private static partial void LogTooLarge(ILogger logger, string path, int actual, int cap);

    [LoggerMessage(Level = LogLevel.Error, Message = "Script draft save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script draft delete failed: {Path}")]
    private static partial void LogClearFailed(ILogger logger, Exception ex, string path);

    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("draft")] public ScriptDraftDto? Draft { get; set; }
    }
}
