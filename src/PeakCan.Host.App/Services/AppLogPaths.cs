using System.IO;

namespace PeakCan.Host.App.Services;

/// <summary>
/// 应用日志落盘位置。
/// <para>
/// **与 <c>appsettings.json</c> 的 <c>Serilog:WriteTo[0]:Args.path</c>
/// （<c>%LOCALAPPDATA%/PeakCan.Host/logs/peak-.log</c>，按天滚动）同源。**
/// 改 Serilog 配置时必须同步改这里，否则「查看日志」会指向一个空目录。
/// 要根治得把 Serilog 配置反查进 DI，那是另一件事。
/// </para>
/// </summary>
public static class AppLogPaths
{
    /// <summary>日志目录（默认配置下的真实位置）。</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host",
        "logs");

    /// <summary>默认日志目录里最新的 <c>peak-*.log</c>，没有则 null。</summary>
    public static string? NewestLogFile() => NewestLogFile(LogDirectory);

    /// <summary>
    /// 指定目录里最新的 <c>peak-*.log</c>，没有则 null。
    /// 公开是为了让"选最新一份"这条逻辑能用临时目录单测（不必依赖本机日志）。
    /// </summary>
    public static string? NewestLogFile(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        string? newest = null;
        var newestAt = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(directory, "peak-*.log"))
        {
            var writtenAt = File.GetLastWriteTimeUtc(file);
            if (newest is null || writtenAt > newestAt)
            {
                newest = file;
                newestAt = writtenAt;
            }
        }
        return newest;
    }
}
