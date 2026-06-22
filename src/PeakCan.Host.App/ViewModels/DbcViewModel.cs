using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// View-model for the DBC tab (<c>DbcView.xaml</c>). Owns the
/// <see cref="Messages"/> collection bound to the DataGrid and a
/// status string surfaced next to the Open button.
/// <para>
/// <b>Event wiring:</b> subscribes to <see cref="DbcService.DbcLoaded"/>
/// and <see cref="DbcService.LoadFailed"/> in the constructor. The
/// shell registers the VM as a DI singleton that lives for the whole
/// app, so the event subscriptions are intentionally NEVER unsubscribed
/// — a previous <see cref="IDisposable"/> implementation was a latent
/// footgun (see review Task 15 fix-history). Both <c>DbcService</c> and
/// <c>DbcViewModel</c> die together at process exit, so GC + finalizer
/// pass handles cleanup.
/// </para>
/// <para>
/// <b>Dispatcher marshaling:</b> <see cref="DbcService.LoadAsync"/> runs
/// the parse on a worker thread and raises the events on that worker.
/// Any mutation of an <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// bound to a WPF <c>ItemsControl</c> MUST happen on the UI dispatcher
/// (cross-thread <c>CollectionChanged</c> throws
/// <c>NotSupportedException</c>). Both <see cref="OnLoaded"/> and
/// <see cref="OnLoadFailed"/> marshal via <see cref="System.Windows.Application.Current"/>.
/// In the xunit test context (no <c>Application</c>) we fall back to
/// running inline so the test can still observe the post-state.
/// </para>
/// <para>
/// <b>OpenCommand testability:</b> the production path pops a WPF
/// <see cref="OpenFileDialog"/>. The dialog itself cannot be exercised
/// from xunit (no STA / no <c>Application</c>); tests instead drive the
/// <see cref="DbcService"/> events directly to cover the resulting
/// state transitions.
/// </para>
/// </summary>
public sealed partial class DbcViewModel : ObservableObject
{
    private readonly DbcService _svc;
    private readonly SignalViewModel _signals;
    private readonly ILogger<DbcViewModel> _logger;
    private readonly IFileDialogService _fileDialog;

    /// <summary>
    /// Rows bound to the DataGrid. Replaced on every successful load
    /// (Clear + Add) so the UI reflects the most recent parse.
    /// </summary>
    public ObservableCollection<DbcMessageViewModel> Messages { get; } = new();

    /// <summary>Last successfully loaded file path, or empty.</summary>
    [ObservableProperty]
    private string _loadedPath = "";

    /// <summary>Status string surfaced next to the Open button.</summary>
    [ObservableProperty]
    private string _status = "No DBC loaded";

    public DbcViewModel(DbcService svc, SignalViewModel signals, ILogger<DbcViewModel> logger,
                        IFileDialogService? fileDialog = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // v0.7.0: optional file dialog service. When null, OpenAsync
        // falls back to the legacy WPF OpenFileDialog path (backwards
        // compatible with existing tests that don't inject one).
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        _svc.DbcLoaded += OnLoaded;
        _svc.LoadFailed += OnLoadFailed;
    }

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
            foreach (var m in doc.Messages)
            {
                Messages.Add(DbcMessageViewModel.From(m));
            }
            // Task 16: clear the decoded-signal table so stale entries
            // from a previous parse do not linger against a new DBC load.
            _signals.Reset();
            // v0.6.0: wire DBC service for value-table lookups.
            _signals.SetDbcService(_svc);
            var fileName = string.IsNullOrEmpty(LoadedPath) ? "(memory)" : Path.GetFileName(LoadedPath);
            Status = $"Loaded {doc.Messages.Count} messages from {fileName}";
        })).RunOnUi();
    }

    private void OnLoadFailed(PeakCan.Host.Core.Error error)
    {
        // Same dispatcher marshaling rationale as OnLoaded. Status is
        // bound to the UI; marshal via the same chokepoint.
        ((Action)(() => Status = $"FAIL: {error.Code} {error.Message}")).RunOnUi();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DBC Open invoked for {Path}")]
    private static partial void LogOpenInvoked(ILogger logger, string path);
}