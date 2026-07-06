using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Panel VM for the top Session header strip: session set / TesterPresent
/// toggle / SecurityAccess. Holds no row collection; the orchestrator
/// constructs it with the shared OutputLog via AttachLog.
/// </summary>
public sealed partial class SessionPanelViewModel : ObservableObject, IUdsPanel, IDisposable
{
    private readonly UdsClient _udsClient;
    private readonly ILogger<SessionPanelViewModel> _logger;
    private readonly SynchronizationContext? _syncContext;
    private CancellationTokenSource? _testerPresentCts;
    private ObservableCollection<UdsLogLine>? _log;

    [ObservableProperty] private string _currentSession     = "Default";
    [ObservableProperty] private byte?  _securityLevel;
    [ObservableProperty] private bool   _testerPresentActive;

    public SessionPanelViewModel(UdsClient udsClient, ILogger<SessionPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        ArgumentNullException.ThrowIfNull(logger);
        _udsClient = udsClient;
        _logger    = logger;
        // v3.8.7 PATCH H3: capture the WPF SynchronizationContext at
        // construction so the TesterPresent background loop's catch arm
        // can marshal UI mutations (AppendLog mutation of bound
        // ObservableCollection, TesterPresentActive = false raising
        // PropertyChanged) back to the UI thread. Mirrors the pattern
        // in ReplayViewModel.OnPlaybackEnded (lines 1062-1071). Without
        // this, the WPF dispatcher rejects the cross-thread CollectionChanged
        // and DataTrigger raises NotSupportedException.
        _syncContext = SynchronizationContext.Current;
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    [RelayCommand]
    private Task SetDefaultSessionAsync()    => SetSessionAsync(0x01, "Default");
    [RelayCommand]
    private Task SetExtendedSessionAsync()   => SetSessionAsync(0x02, "Extended");
    [RelayCommand]
    private Task SetProgrammingSessionAsync() => SetSessionAsync(0x03, "Programming");

    [RelayCommand]
    private void ToggleTesterPresent()
    {
        if (TesterPresentActive)
        {
            _testerPresentCts?.Cancel();
            _testerPresentCts?.Dispose();
            _testerPresentCts = null;
            TesterPresentActive = false;
            AppendLog("Info", "TesterPresent stopped");
            return;
        }
        _testerPresentCts = new CancellationTokenSource();
        var ct = _testerPresentCts.Token;
        TesterPresentActive = true;
        AppendLog("Info", "TesterPresent started (2s interval)");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _udsClient.TesterPresentAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* expected on stop */ }
            catch (Exception ex)
            {
                // v3.8.7 PATCH H3: marshal the post-failure UI mutations
                // back to the captured SyncContext so WPF does not
                // throw on cross-thread CollectionChanged +
                // PropertyChanged. In test context (SyncContext == null),
                // fall through to direct call like ReplayViewModel does.
                if (_syncContext is not null)
                {
                    _syncContext.Post(_ =>
                    {
                        AppendLog("Error", $"TesterPresent loop error: {ex.Message}");
                        TesterPresentActive = false;
                    }, null);
                }
                else
                {
                    AppendLog("Error", $"TesterPresent loop error: {ex.Message}");
                    TesterPresentActive = false;
                }
            }
        }, ct);
    }

    [RelayCommand]
    private async Task SecurityAccessAsync()
    {
        try
        {
            AppendLog("Info", "Requesting security access (level 0x01)...");
            // v2.0.6 PATCH Bug-3: no ConfigureAwait(false) — catch handlers set
            // SecurityLevel and call AppendLog, both of which need the UI
            // dispatcher.
            var response = await _udsClient.SecurityAccessAsync((byte)0x01, CancellationToken.None);
            SecurityLevel = 0x01;
            AppendLog("Info", $"SecurityAccess 0x01 succeeded ({response.Length} bytes).");
        }
        catch (KeyAlgorithmNotConfiguredException ex)
        {
            LogKeyAlgorithmNotConfigured(_logger, ex, ex.SecurityLevel);
            AppendLog("Warn", ex.Message);
            AppendLog("Info", "Hint: register an IKeyDerivationAlgorithm implementation in DI before invoking SecurityAccess.");
            SecurityLevel = null;
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Security access failed: NRC 0x{(byte)ex.ResponseCode:X2}");
            SecurityLevel = null;
        }
        catch (InvalidOperationException ex)
        {
            AppendLog("Warn", ex.Message);
            AppendLog("Info", "Hint: register an IKeyDerivationAlgorithm implementation in DI before invoking SecurityAccess.");
            SecurityLevel = null;
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Security access error: {ex.Message}");
            SecurityLevel = null;
        }
    }

    private async Task SetSessionAsync(byte subFunction, string label)
    {
        try
        {
            // v2.0.6 PATCH Bug-3: no ConfigureAwait(false) — AppendLog writes to
            // the shared ObservableCollection on this continuation path.
            await _udsClient.DiagnosticSessionControlAsync(subFunction);
            CurrentSession = label;
            AppendLog("Info", $"Session → {label}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Set session {label} failed: {ex.Message}");
        }
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine($"{DateTime.Now:HH:mm:ss.fff}", level, message));

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning,
        Message = "SecurityAccess key algorithm not configured for level 0x{Level:X2}")]
    private static partial void LogKeyAlgorithmNotConfigured(ILogger logger, Exception ex, byte level);

    public void Dispose()
    {
        // Reset the public flag BEFORE cancelling the CTS so observers see
        // TesterPresentActive flip to false before the background loop
        // observes cancellation. Without this, callers that read the flag
        // after Dispose would see a stale "running" state.
        TesterPresentActive = false;
        _testerPresentCts?.Cancel();
        _testerPresentCts?.Dispose();
        _testerPresentCts = null;
        GC.SuppressFinalize(this);
    }
}

