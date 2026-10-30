using System.Diagnostics;
using System.IO;

namespace PeakCan.Host.App.Services;

/// <summary>
/// 「在资源管理器里定位 / 打开目录」这类外壳动作的接缝。
/// <para>
/// 单拆一个接口是为了让"打开目录"能被单测：<see cref="Process.Start(ProcessStartInfo)"/>
/// 在 xunit 里既不能断言也不能回滚。<see cref="ExplorerShellLauncher"/> 是唯一的
/// 生产实现，测试注入替身。
/// </para>
/// </summary>
public interface IShellLauncher
{
    /// <summary>在资源管理器中选中 <paramref name="path"/>。路径不存在时抛 <see cref="FileNotFoundException"/>。</summary>
    void Reveal(string path);

    /// <summary>用默认关联打开文件夹。</summary>
    void OpenFolder(string folderPath);
}

/// <summary>生产实现：调用 <c>explorer.exe</c>。</summary>
public sealed class ExplorerShellLauncher : IShellLauncher
{
    /// <inheritdoc />
    public void Reveal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // 路径没了就别让 explorer 去开「文档」——那看起来像成功了，实际什么都没定位到。
        // 抛出去交给调用方显示，不在这里吞掉。
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException($"路径不存在：{path}", path);

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <inheritdoc />
    public void OpenFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"目录不存在：{folderPath}");

        Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
    }
}
