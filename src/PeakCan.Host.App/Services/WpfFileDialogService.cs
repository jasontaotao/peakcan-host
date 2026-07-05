using Microsoft.Win32;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

/// <summary>
/// WPF implementation of <see cref="IFileDialogService"/>. Pops the
/// appropriate file dialog (open or save) and returns the selected
/// path or null on cancellation.
/// </summary>
public sealed class WpfFileDialogService : IFileDialogService
{
    public string? ShowOpenDialog(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// v3.5.0 MINOR: shows a <see cref="SaveFileDialog"/> with the
    /// supplied filter + default extension. <c>OverwritePrompt = true</c>
    /// is the WPF default and surfaces the system "file exists,
    /// overwrite?" confirmation.
    /// </summary>
    public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory)
    {
        var dlg = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = defaultExt ?? "",
            OverwritePrompt = true,
        };
        if (!string.IsNullOrEmpty(initialDirectory))
            dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}