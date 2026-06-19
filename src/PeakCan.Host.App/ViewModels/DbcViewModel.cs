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
/// and <see cref="DbcService.LoadFailed"/> in the constructor;
/// <see cref="Dispose"/> unsubscribes. The shell registers the VM as a
/// singleton so the service + VM live for the whole app — Dispose is
/// called at process shutdown via <see cref="AppHostBuilder"/>'s host
/// disposal path (Task 17 may wire an explicit shutdown hook).
/// </para>
/// <para>
/// <b>OpenCommand testability:</b> the production path pops a WPF
/// <see cref="OpenFileDialog"/>. The dialog itself cannot be exercised
/// from xunit (no STA / no <c>Application</c>); tests instead drive the
/// <see cref="DbcService"/> events directly to cover the resulting
/// state transitions.
/// </para>
/// </summary>
public sealed partial class DbcViewModel : ObservableObject, IDisposable
{
    private readonly DbcService _svc;
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

    public DbcViewModel(DbcService svc, ILogger<DbcViewModel> logger)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
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
        Messages.Clear();
        foreach (var m in doc.Messages)
        {
            Messages.Add(DbcMessageViewModel.From(m));
        }
        var fileName = string.IsNullOrEmpty(LoadedPath) ? "(memory)" : Path.GetFileName(LoadedPath);
        Status = $"Loaded {doc.Messages.Count} messages from {fileName}";
    }

    private void OnLoadFailed(PeakCan.Host.Core.Error error)
    {
        Status = $"FAIL: {error.Code} {error.Message}";
    }

    /// <summary>
    /// Unsubscribe from the singleton <see cref="DbcService"/> events so
    /// the VM does not outlive the service. Idempotent.
    /// </summary>
    public void Dispose()
    {
        _svc.DbcLoaded -= OnLoaded;
        _svc.LoadFailed -= OnLoadFailed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DBC Open invoked for {Path}")]
    private static partial void LogOpenInvoked(ILogger logger, string path);
}