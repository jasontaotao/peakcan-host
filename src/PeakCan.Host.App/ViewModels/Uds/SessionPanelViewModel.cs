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

    /// <summary>
    /// ECU **已授予**的安全等级：只在 SecurityAccess 成功后写入。
    /// <para>
    /// 必须和 <see cref="SecurityLevel"/> 分开：后者同时是「等级输入框的绑定值」，
    /// 用户在框里敲 0B 但没点 SecurityAccess 时 ECU 根本没解锁——徽章若直接投影
    /// 它就会谎报"已解锁"，操作者据此跳过解锁，后续写操作全部 NRC 0x33。
    /// </para>
    /// </summary>
    [ObservableProperty] private byte? _grantedSecurityLevel;

    /// <summary>安全等级展示文案（主窗口徽章与 UDS 窗口头部共用）。</summary>
    public string SecurityLevelText => GrantedSecurityLevel is { } level ? $"等级 {level}" : "未解锁";

    partial void OnGrantedSecurityLevelChanged(byte? value)
        => OnPropertyChanged(nameof(SecurityLevelText));

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
            StopTesterPresentLoop();
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
            AppendLog("Info", $"Requesting security access (level 0x{SecurityLevel:X2})...");
            // v2.0.6 PATCH Bug-3: no ConfigureAwait(false) — catch handlers set
            // SecurityLevel and call AppendLog, both of which need the UI
            // dispatcher.
            // Phase 2: level is now operator-configurable via SecurityLevel property.
            var level = SecurityLevel ?? 0x01;
            var response = await _udsClient.SecurityAccessAsync(level, CancellationToken.None);
            SecurityLevel = level;  // reflect the level that was actually used
            GrantedSecurityLevel = level;   // 只有这里写"已授予"
            AppendLog("Info", $"SecurityAccess 0x{level:X2} succeeded ({response.Length} bytes).");
        }
        catch (KeyAlgorithmNotConfiguredException ex)
        {
            LogKeyAlgorithmNotConfigured(_logger, ex, ex.SecurityLevel);
            AppendLog("Warn", ex.Message);
            AppendLog("Info", "Hint: register an IKeyDerivationAlgorithm implementation in DI before invoking SecurityAccess.");
            SecurityLevel = null;
            GrantedSecurityLevel = null;
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Security access failed: NRC 0x{(byte)ex.ResponseCode:X2}");
            SecurityLevel = null;
            GrantedSecurityLevel = null;
        }
        catch (InvalidOperationException ex)
        {
            AppendLog("Warn", ex.Message);
            AppendLog("Info", "Hint: register an IKeyDerivationAlgorithm implementation in DI before invoking SecurityAccess.");
            SecurityLevel = null;
            GrantedSecurityLevel = null;
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Security access error: {ex.Message}");
            SecurityLevel = null;
            GrantedSecurityLevel = null;
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
            // ISO 14229：任何会话切换都会终止已授予的安全访问。
            GrantedSecurityLevel = null;
            AppendLog("Info", $"Session → {label}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Set session {label} failed: {ex.Message}");
        }
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine($"{DateTime.Now:HH:mm:ss.fff}", level, message));

    /// <summary>
    /// 停掉保活循环（cancel + dispose + 复位标志）。幂等：没在跑时调用是安全的，
    /// 只是什么都不做——TesterPresentActive 本来就已经是 false。
    /// </summary>
    private void StopTesterPresentLoop()
    {
        _testerPresentCts?.Cancel();
        _testerPresentCts?.Dispose();
        _testerPresentCts = null;
        TesterPresentActive = false;
    }

    /// <summary>
    /// P1 防事故：刷写开始，立刻停保活循环。
    /// <para>
    /// TesterPresent 每 2 秒往诊断 CAN-ID 发 <c>3E 00</c>，刷写用的是编程会话——
    /// 两者抢同一个 ECU，轻则刷写失败重则 ECU 停在半截。禁用按钮管不住已经在跑的循环，
    /// 所以这里必须真停。
    /// </para>
    /// <para>
    /// **刻意不自动恢复**：刷写收尾常跟着 ECU 复位/擦除后处理，这时注入 3E 会打断收尾。
    /// 宁可让用户刷完再明确点一次，也不替他做这个决定。
    /// </para>
    /// </summary>
    public void SuspendForFlash()
    {
        // 刷写走副栈，会把 ECU 切到编程会话 → 诊断侧已授予的安全等级失效。
        GrantedSecurityLevel = null;
        if (!TesterPresentActive) return;
        StopTesterPresentLoop();
        AppendLog("Warn", "TesterPresent 已因刷写暂停（刷写结束后不会自动恢复，需要时请重新开启）");
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning,
        Message = "SecurityAccess key algorithm not configured for level 0x{Level:X2}")]
    private static partial void LogKeyAlgorithmNotConfigured(ILogger logger, Exception ex, byte level);

    /// <summary>
    /// Window-level halt (v3.49.x PATCH plan T2): called by
    /// <c>UdsWindow.Unloaded</c> when the UDS diagnostic window closes. Stops the
    /// TesterPresent background loop (cancel its CTS) and flips
    /// <see cref="TesterPresentActive"/> to false so the bound checkbox reflects
    /// the true state. <b>Non-terminating</b> — unlike a one-shot
    /// <see cref="IDisposable"/>, this leaves the VM reusable: <see cref="SessionPanelViewModel"/>
    /// is a DI singleton (<c>AppHostBuilder.cs:261</c>) shared across window open/close
    /// cycles, so a close must leave it ready to re-arm the loop for the next window
    /// instance that binds it.
    /// </summary>
    /// <summary>
    /// 通道断开后 ECU 侧状态不可知。<see cref="GrantedSecurityLevel"/> 按"未解锁"
    /// 处理：错报已解锁会误导后续写操作，错报未解锁只是让用户多点一次解锁。
    /// </summary>
    public void ResetForConnectionChange() => GrantedSecurityLevel = null;

    public void StopForWindowClose()
    {
        // Reset the public flag BEFORE cancelling the CTS so observers see
        // TesterPresentActive flip to false before the background loop
        // observes cancellation. Without this, callers that read the flag
        // after stop would see a stale "running" state.
        TesterPresentActive = false;
        _testerPresentCts?.Cancel();
        _testerPresentCts?.Dispose();
        _testerPresentCts = null;
    }

    public void Dispose() => StopForWindowClose();
}

