namespace PeakCan.Host.Core;

/// <summary>
/// Abstraction over file-open dialogs so the App-layer ViewModels can
/// be tested without WPF's <c>OpenFileDialog</c> (which requires an
/// STA thread and <c>Application</c> instance). Production injects
/// the WPF implementation; tests inject a fake that returns a canned
/// path or simulates cancellation.
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Show an open-file dialog. Returns the selected path, or null
    /// if the user cancelled.
    /// </summary>
    string? ShowOpenDialog(string filter);

    /// <summary>
    /// v3.5.0 MINOR: show a save-file dialog. Returns the chosen path,
    /// or null if the user cancelled. <paramref name="filter"/> uses
    /// the Win32 <c>OpenFileDialog.Filter</c> syntax
    /// ("Display|*.ext;*.EXT|All files|*.*").
    /// <paramref name="defaultExt"/> is appended when the typed name
    /// has no extension. <paramref name="initialDirectory"/> seeds the
    /// dialog's start location; null uses the WPF default.
    /// </summary>
    string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory);
}
