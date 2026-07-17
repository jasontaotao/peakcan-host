using System.IO;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: DBC loading (OpenAsync + event handlers).
    // Methods moved verbatim from DbcViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Ctor -> _svc.DbcLoaded += OnLoaded; _svc.LoadFailed += OnLoadFailed (main ctor subscriptions)
    //   - OpenAsync -> _fileDialog.ShowOpenDialog (main field) + LogOpenInvoked (main [LoggerMessage])
    //   - OnLoaded -> Messages.Clear + FilteredMessages.Clear + _allMessages (main fields) + ApplyFilter (Flow B)
    //   - OnLoaded -> _signals.Reset + _signals.SetDbcService (main field)
    //   - OnLoaded -> TotalMessages + TotalSignals + Status (main [ObservableProperty])
    //   - OnLoadFailed -> Status (main [ObservableProperty])
    //
    // [RelayCommand] attribute MUST travel with OpenAsync method.

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = _fileDialog.ShowOpenDialog("DBC files (*.dbc)|*.dbc|All files|*.*");
        if (path is null) return;
        LoadedPath = path;
        Status = "Parsing...";
        LogOpenInvoked(_logger, path);
        await _svc.LoadAsync(path).ConfigureAwait(true);
    }

    private void OnLoaded(DbcDocument doc)
    {
        // DbcService.LoadAsync raises this event on its worker thread.
        // ObservableCollection<T>.CollectionChanged must fire on the UI
        // dispatcher when the collection is bound to an ItemsControl
        // (DataGrid, ListBox, etc.) — cross-thread mutation throws
        // NotSupportedException ("This type of CollectionView does not
        // support changes to its SourceCollection from a thread different
        // from the Dispatcher thread"). The marshal chokepoint lives in
        // DispatcherExtensions.RunOnUi; the previous Task 19 guard
        // (`appDispatcher == callingDispatcher`) was inverted and silently
        // skipped the hop in production. See
        // DispatcherExtensions.cs class doc-comment for the regression.
        ((Action)(() =>
        {
            Messages.Clear();
            _allMessages.Clear();
            FilteredMessages.Clear();
            foreach (var m in doc.Messages)
            {
                var vm = DbcMessageViewModel.From(m);
                Messages.Add(vm);
                _allMessages.Add(vm);
            }
            ApplyFilter();
            // Task 16: clear the decoded-signal table so stale entries
            // from a previous parse do not linger against a new DBC load.
            _signals.Reset();
            // v0.6.0: wire DBC service for value-table lookups.
            _signals.SetDbcService(_svc);
            TotalMessages = doc.Messages.Count;
            TotalSignals = doc.Messages.Sum(m => m.Signals.Count);
            var fileName = string.IsNullOrEmpty(LoadedPath) ? "(memory)" : Path.GetFileName(LoadedPath);
            Status = $"Loaded {TotalMessages} messages, {TotalSignals} signals from {fileName}";
        })).RunOnUi();
    }

    private void OnLoadFailed(PeakCan.Host.Core.Error error)
    {
        // Same dispatcher marshaling rationale as OnLoaded. Status is
        // bound to the UI; marshal via the same chokepoint.
        ((Action)(() => Status = $"FAIL: {error.Code} {error.Message}")).RunOnUi();
    }
}