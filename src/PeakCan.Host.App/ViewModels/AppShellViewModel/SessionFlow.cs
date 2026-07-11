using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow C: Session open/save + recent-sessions list (v3.6.0 MINOR T3 + later).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - OpenSessionAsync -> TraceViewerViewModel.OpenSessionAsync (cross-class)
    //   - SaveSessionAsync -> TraceViewerViewModel.SaveSessionAsync (cross-class)
    //   - OpenRecentSessionAsync -> TraceViewerViewModel.OpenSessionAsync (cross-class)
    //   - All 4 -> _recentSessions.Add (DI field, main file)

    [RelayCommand]
    private void OpenDbc()
    {
        // Task 15: the File ▸ Open DBC... menu item routes into the DBC
        // tab. The actual file-open dialog is owned by the per-view
        // Open button inside DbcViewModel.OpenAsync; the menu item
        // only navigates so the user sees the right surface.
        CurrentView = GetOrCreateDbcView();
        LogOpenDbcInvoked(_logger);
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Open Session... menu command. Pops a
    /// file-open dialog (via the WPF-independent <see cref="IFileDialogService"/>),
    /// loads the chosen bundle through <see cref="TraceViewerViewModel.OpenSessionAsync"/>,
    /// surfaces any missing <c>.asc</c> recordings via MessageBox, and
    /// records the path in the MRU list. Cancellation returns silently.
    /// </summary>
    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        var path = _fileDialogs.ShowOpenDialog(
            "Trace Viewer session|*.tmtrace;*.TMTRACE|All files|*.*");
        if (string.IsNullOrEmpty(path)) return;
        var missing = await _traceViewerViewModel.OpenSessionAsync(path)
            .ConfigureAwait(true);
        if (missing.Count > 0)
        {
            // v3.10.0 MINOR T1 (C1): route through IMessageBoxPrompt
            // instead of MessageBox.Show so the VM stays unit-testable.
            // The WPF impl mirrors the previous OK + Warning image
            // contract; tests substitute a fake to assert invocation.
            await _messageBoxPrompt.ShowInformationAsync(
                "Open Session",
                $"These .asc files are missing:\n{string.Join("\n", missing)}",
                Application.Current?.MainWindow)
                .ConfigureAwait(true);
        }
        _recentSessions.Add(path, "trace");
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Save Session... menu command. Pops a
    /// file-save dialog, hands the chosen path to
    /// <see cref="TraceViewerViewModel.SaveSessionAsync"/>, then records
    /// it in the MRU list. The Trace Viewer window must be open and
    /// hold the session state being saved; we do not auto-open it here
    /// (matches the toolbar behaviour that the menu is replacing).
    /// </summary>
    [RelayCommand]
    private async Task SaveSessionAsync()
    {
        var path = _fileDialogs.ShowSaveDialog(
            "Trace Viewer session|*.tmtrace|All files|*.*",
            ".tmtrace",
            null);
        if (string.IsNullOrEmpty(path)) return;
        await _traceViewerViewModel.SaveSessionAsync(path)
            .ConfigureAwait(true);
        _recentSessions.Add(path, "trace");
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Open Recent ▸ &lt;name&gt; menu command.
    /// <paramref name="path"/> is the CommandParameter wired through
    /// the <c>DataTemplate</c> in <c>AppShell.xaml</c>. Skips the file
    /// dialog (the path was chosen from the MRU), forwards to
    /// <see cref="TraceViewerViewModel.OpenSessionAsync"/>, and
    /// re-records the path so a re-click moves it back to the top of
    /// the list (matching standard MRU UX).
    /// </summary>
    [RelayCommand]
    private async Task OpenRecentSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var missing = await _traceViewerViewModel.OpenSessionAsync(path)
            .ConfigureAwait(true);
        if (missing.Count > 0)
        {
            // v3.10.0 MINOR T1 (C1): see OpenSessionAsync — same
            // IMessageBoxPrompt seam, distinct title so the user
            // can tell which menu path triggered the warning.
            await _messageBoxPrompt.ShowInformationAsync(
                "Open Recent Session",
                $"These .asc files are missing:\n{string.Join("\n", missing)}",
                Application.Current?.MainWindow)
                .ConfigureAwait(true);
        }
        _recentSessions.Add(path, "trace");
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Clear Recent menu command. Wipes the
    /// Trace entries only — replay entries (added by the Replay tab's
    /// own submenu in chunk 2) survive. The on-disk JSON file is left
    /// alone unless the list became empty as a side effect.
    /// </summary>
    [RelayCommand]
    private void ClearRecentSessions() => _recentSessions.Clear("trace");

    /// <summary>
    /// v3.6.0 MINOR T3: rebuild <see cref="RecentSessionEntries"/> from
    /// <see cref="RecentSessionsService.Recent"/>. Called on
    /// <see cref="RecentSessionsService"/> PropertyChanged (any
    /// mutation) and once after the initial LoadAsync. Cheap (max 5
    /// entries) — full Clear + rebuild avoids the per-item
    /// CollectionChanged dance.
    /// <para>
    /// v3.7.0 MINOR Chunk 2: the AppShell menu now filters to Trace
    /// entries only. Empty <c>ViewType</c> is the legacy-trace value
    /// carried over from v3.6.0–v3.6.4 entries (which pre-date the
    /// field); treating it as trace preserves the user's pre-existing
    /// MRU list across the upgrade.
    /// </para>
    /// </summary>
    private void RefreshRecentEntries()
    {
        RecentSessionEntries.Clear();
        foreach (var r in _recentSessions.Recent)
        {
            // v3.7.0: filter to Trace Viewer entries only. Empty ViewType
            // is legacy-trace (v3.6.x saves) — kept for back-compat.
            if (r.ViewType != "trace" && r.ViewType != "")
                continue;
            RecentSessionEntries.Add(new RecentSessionVm(r.Path, r.Label));
        }
    }
}