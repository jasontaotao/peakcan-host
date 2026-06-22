using Microsoft.Win32;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

/// <summary>
/// WPF implementation of <see cref="IFileDialogService"/>. Pops a
/// standard <see cref="OpenFileDialog"/> and returns the selected
/// path or null on cancellation.
/// </summary>
public sealed class WpfFileDialogService : IFileDialogService
{
    public string? ShowOpenDialog(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
