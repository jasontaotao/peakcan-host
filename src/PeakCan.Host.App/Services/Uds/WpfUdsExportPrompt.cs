using System.IO;
using System.Windows;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// 生产环境的导出交互：SaveFileDialog 选路径/格式，MessageBox 三选一决定脱敏。
/// 与 <see cref="IUdsExportPrompt"/> 分离，VM 单测注入假实现。
/// </summary>
public sealed class WpfUdsExportPrompt(IFileDialogService dialogs) : IUdsExportPrompt
{
    public string? ChooseSavePath(out bool isCsv)
    {
        // 先定格式再开保存框：只靠过滤器选择无法改变 DefaultExt，
        // 用户不手敲 .csv 就会得到 Markdown 内容却叫 .md（spec §13.2）。
        var format = MessageBox.Show(
            "导出格式？\n\n是：CSV（每行一个事务）\n否：Markdown 报告\n取消：不导出",
            "导出报告", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (format == MessageBoxResult.Cancel)
        {
            isCsv = false;
            return null;
        }

        isCsv = format == MessageBoxResult.Yes;
        var path = dialogs.ShowSaveDialog(
            isCsv ? "CSV (*.csv)|*.csv" : "Markdown (*.md)|*.md",
            isCsv ? ".csv" : ".md",
            initialDirectory: null);
        if (path is null) return null;

        // 用户手敲了别的扩展名时按所选格式纠正。
        var desired = isCsv ? ".csv" : ".md";
        if (!Path.GetExtension(path).Equals(desired, StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, desired);
        return path;
    }

    public bool ConfirmOverwrite(string path)
        => MessageBox.Show(
               $"文件已存在：\n{path}\n\n要覆盖吗？",
               "导出报告", MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;

    public UdsExportChoice AskRedaction()
    {
        // 每次导出都必须让用户显式选择；Esc/取消 = 不导出。
        var result = MessageBox.Show(
            "导出内容是否脱敏？\n\n是：隐藏路径、种子/密钥与 DID 载荷\n否：原样导出到本地文件\n取消：不导出",
            "导出报告", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => UdsExportChoice.Redacted,
            MessageBoxResult.No => UdsExportChoice.Raw,
            _ => UdsExportChoice.Cancel,
        };
    }
}
