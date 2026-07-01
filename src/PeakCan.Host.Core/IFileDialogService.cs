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
}
