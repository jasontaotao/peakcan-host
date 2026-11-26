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
        var path = dialogs.ShowSaveDialog(
            "Markdown (*.md)|*.md|CSV (*.csv)|*.csv", ".md", initialDirectory: null);
        isCsv = path is not null
            && Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);
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
