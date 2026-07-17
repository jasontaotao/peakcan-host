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

    /// <summary>
    /// Search text for filtering DBC messages. Matches message name
    /// or sender (case-insensitive substring). Empty = show all.
    /// </summary>
    [ObservableProperty]
    private string _searchText = "";

    // Full list of messages from the last successful load.
    private readonly List<DbcMessageViewModel> _allMessages = new();

    /// <summary>Filtered view of Messages based on SearchText.</summary>
    public ObservableCollection<DbcMessageViewModel> FilteredMessages { get; } = new();

    /// <summary>Total number of messages in the loaded DBC.</summary>
    [ObservableProperty]
    private int _totalMessages;

    /// <summary>Total number of signals across all messages.</summary>
    [ObservableProperty]
    private int _totalSignals;

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


    [LoggerMessage(Level = LogLevel.Information, Message = "DBC Open invoked for {Path}")]
    private static partial void LogOpenInvoked(ILogger logger, string path);

    /// <summary>
    /// Called when <see cref="SearchText"/> changes. Filters the
    /// <see cref="FilteredMessages"/> collection.
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        var pattern = SearchText.AsSpan().Trim();
        foreach (var m in _allMessages)
        {
            if (pattern.Length == 0
                || m.Name.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Sender.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                FilteredMessages.Add(m);
            }
        }
    }

    /// <summary>
    /// Export DBC messages to a CSV file.
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        if (_allMessages.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "dbc-messages.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ID,Name,DLC,Sender,Signals");
        foreach (var m in _allMessages)
        {
            sb.AppendLine(string.Join(',',
                m.Id,
                m.Name,
                m.Dlc,
                m.Sender,
                m.SignalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
    }
}