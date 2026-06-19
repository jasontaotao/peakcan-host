using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PeakCan.Host.App.Services;
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

    public DbcViewModel(DbcService svc, SignalViewModel signals, ILogger<DbcViewModel> logger)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _svc.DbcLoaded += OnLoaded;
        _svc.LoadFailed += OnLoadFailed;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog { Filter = "DBC files (*.dbc)|*.dbc|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        LoadedPath = dlg.FileName;
        Status = "Parsing...";
        LogOpenInvoked(_logger, dlg.FileName);
        await _svc.LoadAsync(dlg.FileName).ConfigureAwait(true);
    }

    private void OnLoaded(DbcDocument doc)
    {
        // DbcService.LoadAsync raises this event on its worker thread.
        // ObservableCollection<T>.CollectionChanged must fire on the UI
        // dispatcher when the collection is bound to an ItemsControl
        // (DataGrid, ListBox, etc.) — cross-thread mutation throws
        // NotSupportedException. Marshal before mutating Messages.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnLoaded(doc));
            return;
        }
        Messages.Clear();
        foreach (var m in doc.Messages)
        {
            Messages.Add(DbcMessageViewModel.From(m));
        }
        // Task 16: clear the decoded-signal table so stale entries from
        // a previous parse do not linger against a new DBC load.
        _signals.Reset();
        var fileName = string.IsNullOrEmpty(LoadedPath) ? "(memory)" : Path.GetFileName(LoadedPath);
        Status = $"Loaded {doc.Messages.Count} messages from {fileName}";
    }

    private void OnLoadFailed(PeakCan.Host.Core.Error error)
    {
        // Same dispatcher marshaling rationale as OnLoaded. PropertyChanged
        // is benign cross-thread (just fires the event), but Status is bound
        // to the UI and we marshal for consistency.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnLoadFailed(error));
            return;
        }
        Status = $"FAIL: {error.Code} {error.Message}";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DBC Open invoked for {Path}")]
    private static partial void LogOpenInvoked(ILogger logger, string path);
}