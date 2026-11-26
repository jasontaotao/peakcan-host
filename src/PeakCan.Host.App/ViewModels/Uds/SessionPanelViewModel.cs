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

    [ObservableProperty] private string _currentSession     = UdsDisplayText.Session(0x01);
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
    private Task SetDefaultSessionAsync()    => SetSessionAsync(0x01, UdsDisplayText.Session(0x01));
    [RelayCommand]
    private Task SetExtendedSessionAsync()   => SetSessionAsync(0x02, UdsDisplayText.Session(0x02));
    [RelayCommand]
    private Task SetProgrammingSessionAsync() => SetSessionAsync(0x03, UdsDisplayText.Session(0x03));

    [RelayCommand]
    private void ToggleTesterPresent()
    {
        if (TesterPresentActive)
        {
            StopTesterPresentLoop();
            AppendLog("Info", "TesterPresent 已停止");
            return;
        }
        _testerPresentCts = new CancellationTokenSource();
        var ct = _testerPresentCts.Token;
        TesterPresentActive = true;
        // 文案从常量派生，不写死秒数——写死的话改了间隔就会变成一句假话。
        AppendLog("Info", $"TesterPresent 已启动（{UdsSession.KeepAliveInterval.TotalSeconds:0.#} 秒周期）");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // 传 ct：断连复位时 StopTesterPresentLoop 只 Cancel/Dispose 不等待，
                    // 若这条请求不响应取消，它会继续持有 UdsClient._requestLock，
                    // 导致 ResetDiagnosticState 拿不到锁、跳过 ISO-TP 复位。
                    await _udsClient.TesterPresentAsync(ct: ct).ConfigureAwait(false);
                    // 与 UdsSession.StartS3KeepAlive 共用同一个间隔常量。此前这里写死
                    // 2 秒，比 S3（5 秒）保守得多，白白浪费总线带宽。
                    await Task.Delay(UdsSession.KeepAliveInterval, ct).ConfigureAwait(false);
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
                        AppendLog("Error", $"TesterPresent 循环出错：{ex.Message}");
                        TesterPresentActive = false;
                    }, null);
                }
                else
                {
                    AppendLog("Error", $"TesterPresent 循环出错：{ex.Message}");
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
            AppendLog("Info", $"请求 SecurityAccess（等级 0x{SecurityLevel:X2}）…");
            // v2.0.6 PATCH Bug-3: no ConfigureAwait(false) — catch handlers set
            // SecurityLevel and call AppendLog, both of which need the UI
            // dispatcher.
            // Phase 2: level is now operator-configurable via SecurityLevel property.
            var level = SecurityLevel ?? 0x01;
            var response = await _udsClient.SecurityAccessAsync(level, CancellationToken.None);
            SecurityLevel = level;  // reflect the level that was actually used
            GrantedSecurityLevel = level;   // 只有这里写"已授予"
            AppendLog("Info", $"SecurityAccess 0x{level:X2} 成功（{response.Length} 字节）。");
        }
        catch (KeyAlgorithmNotConfiguredException ex)
        {
            LogKeyAlgorithmNotConfigured(_logger, ex, ex.SecurityLevel);
            AppendLog("Warn", ex.Message);
            AppendLog("Info", "提示：调用 SecurityAccess 前需在 DI 中注册 IKeyDerivationAlgorithm 实现。");
            SecurityLevel = null;
            GrantedSecurityLevel = null;
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"SecurityAccess 失败：NRC 0x{(byte)ex.ResponseCode:X2}");
            SecurityLevel = null;
            GrantedSecurityLevel = null;
        }
        catch (InvalidOperationException ex)
        {
            AppendLog("Warn", ex.Message);
            AppendLog("Info", "提示：调用 SecurityAccess 前需在 DI 中注册 IKeyDerivationAlgorithm 实现。");
            SecurityLevel = null;
            GrantedSecurityLevel = null;
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"SecurityAccess 出错：{ex.Message}");
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
            AppendLog("Info", $"会话 → {label}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"切换会话 {label} 失败：{ex.Message}");
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
    /// TesterPresent 按 <c>UdsSession.KeepAliveInterval</c> 周期往诊断 CAN-ID 发
    /// <c>3E 00</c>，刷写用的是编程会话——
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
    /// 通道断开/重连时的整体复位。
    /// <para>
    /// 顺序有意为之：先停保活循环——否则复位后它还会继续向旧 ECU 发 <c>3E 00</c>；
    /// 再让 <see cref="UdsClient.ResetDiagnosticState"/> 原子复位会话/时序/ISO-TP/
    /// 在途请求；最后把本 VM 自己的展示状态（当前会话文案、已授予安全等级）拉回基线。
    /// </para>
    /// <para>
    /// <b>不要在这里自己拼 <c>Session.Reset()</c> + <c>Transport.Reset()</c>：</b>
    /// 真正生效的超时在 <c>UdsClient._timer</c>，而 <c>UdsSession</c> 不持有它。
    /// 分开调用会造出「面板显示默认、实际仍用上一台 ECU 的协商值」——比不复位更糟，
    /// 因为界面在说谎。
    /// </para>
    /// </summary>
    public void ResetForConnectionChange()
    {
        if (TesterPresentActive)
            StopTesterPresentLoop();

        _udsClient.ResetDiagnosticState();

        CurrentSession = UdsDisplayText.Session(0x01);
        // 通道断开后 ECU 侧状态不可知，安全等级按「未解锁」处理：
        // 错报已解锁会误导后续写操作，错报未解锁只是让用户多点一次解锁。
        GrantedSecurityLevel = null;
    }

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

