using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.App.Services.Nodes;

/// <summary>节点库文件包装（schema 版本字段，升级不炸）。</summary>
internal sealed record NodeConfigFile(int Version, NodeConfig Config);

/// <summary>
/// 节点角色档案持久化（spec §10）：每节点一个 {Name}.node.json，atomic write（tmp+rename），
/// 仿 <see cref="global::PeakCan.Host.App.Services.Sequence.SequenceLibrary"/>。
/// </summary>
public sealed partial class NodeConfigLibrary
{
    internal static JsonSerializerOptions JsonOpts { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // 测试入口（App.Tests 的 InternalsVisibleTo 已覆盖本程序集）
    internal static JsonSerializerOptions JsonOptsForTests => JsonOpts;

    private readonly string _directory;
    private readonly ILogger<NodeConfigLibrary> _logger;

    public NodeConfigLibrary(ILogger<NodeConfigLibrary>? logger = null)
        : this(DefaultDirectory(), logger)
    {
    }

    internal NodeConfigLibrary(string directory, ILogger<NodeConfigLibrary>? logger = null)
    {
        _directory = directory;
        _logger = logger ?? NullLogger<NodeConfigLibrary>.Instance;
        Directory.CreateDirectory(_directory);
    }

    private static string DefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "nodes");
    }

    public IReadOnlyList<NodeConfig> Load()
    {
        var configs = new List<NodeConfig>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.node.json"))
        {
            try
            {
                var file = JsonSerializer.Deserialize<NodeConfigFile>(File.ReadAllText(path), JsonOpts);
                if (file is { Version: 1, Config: not null })
                    configs.Add(file.Config);
                else
                    LogSkippedCorrupt(_logger, path);
            }
            catch (Exception ex) when (ex is JsonException or System.IO.IOException)
            {
                LogSkippedCorrupt(_logger, path);
            }
        }

        return configs;
    }

    public void Save(NodeConfig config)
    {
        var path = Path.Combine(_directory, Sanitize(config.Name) + ".node.json");
        var json = JsonSerializer.Serialize(new NodeConfigFile(1, config), JsonOpts);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, path);
            throw;
        }
    }

    public bool Delete(string name)
    {
        var path = Path.Combine(_directory, Sanitize(name) + ".node.json");
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>把随应用分发的模板复制到用户库（已存在的不覆盖）。</summary>
    public void EnsureDefaultTemplates(string templateSourceDir)
    {
        if (!Directory.Exists(templateSourceDir))
            return;
        foreach (var src in Directory.EnumerateFiles(templateSourceDir, "*.node.json"))
        {
            var dst = Path.Combine(_directory, Path.GetFileName(src));
            if (!File.Exists(dst))
                File.Copy(src, dst);
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    [LoggerMessage(EventId = 9411, Level = LogLevel.Error, Message = "NodeConfigLibrary save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 9412, Level = LogLevel.Warning, Message = "NodeConfigLibrary skipped corrupt node file {Path}")]
    private static partial void LogSkippedCorrupt(ILogger logger, string path);
}
