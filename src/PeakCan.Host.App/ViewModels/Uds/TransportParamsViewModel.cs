using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Read-only "communication parameters" panel VM for the UDS window. Polls the live
/// timing state of BOTH UDS stacks (the diagnostic stack and, while a flash run is
/// active, the secondary programming stack) and exposes them as bindable strings:
/// <list type="bullet">
/// <item>P2 / P2* — the effective UDS response timeouts, labeled with their provenance
/// (<c>本地默认</c> until a DiagnosticSessionControl positive response overrides them
/// with the ECU-reported values, then <c>ECU 0x10 协商</c>).</item>
/// <item>STmin / BS — the ISO 15765-2 flow-control values the ECU last sent; shown as
/// <c>—</c> until a Flow Control frame has actually been received.</item>
/// <item>N_Bs / N_Cr — the ISO-TP layer's wait-for-FC / wait-for-CF timeouts
/// (code defaults; the App never overrides them).</item>
/// </list>
/// <para>
/// Refresh model: a 500 ms <see cref="DispatcherTimer"/> at Background priority calls
/// <see cref="Poll"/>; tests call <see cref="Poll"/> directly (DispatcherTimer does not
/// fire in STA xunit runs — same pattern as DbcSendViewModel). No editing, no commands:
/// this panel is observability only, it never mutates protocol state.
/// </para>
/// </summary>
public sealed partial class TransportParamsViewModel : ObservableObject
{
    private readonly IsoTpLayer? _diagnosticTransport;
    private readonly UdsClient? _diagnosticClient;
    private readonly FlashPipeline.FlashPanelViewModel? _flash;
    private readonly ILogger<TransportParamsViewModel>? _logger;
    private readonly DispatcherTimer? _pollTimer;

    private const string NotReceived = "— (未收到 FC)";
    private const string NotRunning = "未运行";

    // —— 诊断栈列 ——
    [ObservableProperty] private string _diagTransport = "—";
    [ObservableProperty] private string _diagSession    = "—";
    [ObservableProperty] private string _diagP2         = "—";
    [ObservableProperty] private string _diagP2Star     = "—";
    [ObservableProperty] private string _diagStMin      = NotReceived;
    [ObservableProperty] private string _diagBlockSize  = NotReceived;
    [ObservableProperty] private string _diagNBs        = "—";
    [ObservableProperty] private string _diagNCr        = "—";
    [ObservableProperty] private string _diagS3Failures = "—";

    // —— Flash 副栈列 ——
    [ObservableProperty] private string _flashRunState   = NotRunning;
    [ObservableProperty] private string _flashTransport  = NotRunning;
    [ObservableProperty] private string _flashSession    = NotRunning;
    [ObservableProperty] private string _flashP2         = NotRunning;
    [ObservableProperty] private string _flashP2Star     = NotRunning;
    [ObservableProperty] private string _flashStMin      = NotRunning;
    [ObservableProperty] private string _flashBlockSize  = NotRunning;
    [ObservableProperty] private string _flashNBs        = NotRunning;
    [ObservableProperty] private string _flashNCr        = NotRunning;

    /// <summary>
    /// Production ctor: poll the diagnostic stack (DI singletons) and the flash panel's
    /// in-flight secondary stack. Timer is created stopped — the hosting UdsWindow
    /// starts/stops polling on open/close (DI-singleton lifetime ≠ window lifetime).
    /// </summary>
    public TransportParamsViewModel(
        IsoTpLayer diagnosticTransport,
        UdsClient diagnosticClient,
        FlashPipeline.FlashPanelViewModel flash,
        ILogger<TransportParamsViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(diagnosticTransport);
        ArgumentNullException.ThrowIfNull(diagnosticClient);
        ArgumentNullException.ThrowIfNull(flash);
        _diagnosticTransport = diagnosticTransport;
        _diagnosticClient = diagnosticClient;
        _flash = flash;
        _logger = logger;

        // Initial paint so the panel is correct even before the first tick.
        Poll();

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _pollTimer.Tick += (_, _) => Poll();
        // review 2026-08-29 P2: 不在 ctor 启动——VM 是 DI 单例，进程存活期 2Hz 空转
        // UI 线程（UDS 窗口关闭时也在跑）。改由 UdsWindow 绑定 DataContext 时
        // StartPolling / Unloaded 时 StopPolling（与 Session/Flash 的
        // StopForWindowClose 同一生命周期模式）。
    }

    /// <summary>UDS 窗口打开时由 UdsWindow 调；Disabled 实例（无 timer）为 no-op。</summary>
    public void StartPolling() => _pollTimer?.Start();

    /// <summary>UDS 窗口关闭（Unloaded）时由 UdsWindow 调；可重复调用。</summary>
    public void StopPolling() => _pollTimer?.Stop();

    /// <summary>
    /// Disabled instance for back-compat <see cref="UdsViewModel"/> ctors (tests /
    /// non-DI callers): no stack access, no timer, every field stays at its placeholder.
    /// </summary>
    public static TransportParamsViewModel CreateDisabled() => new();

    private TransportParamsViewModel()
    {
    }

    /// <summary>
    /// Refresh every bindable string from the live stacks. Public so tests can drive
    /// updates without a WPF dispatcher. Swallows read failures into "—" placeholders —
    /// a disposed/partially-torn-down stack must not crash the UI thread tick.
    /// </summary>
    public void Poll()
    {
        if (_diagnosticTransport is null || _diagnosticClient is null || _flash is null)
            return; // disabled instance

        try
        {
            PollDiagnostic();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read diagnostic-stack transport parameters.");
        }

        try
        {
            PollFlashStack();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read flash-stack transport parameters.");
        }
    }

    private void PollDiagnostic()
    {
        var transport = _diagnosticTransport!;
        var session = _diagnosticClient!.Session;
        var config = transport.Config;

        DiagTransport = FormatCanIds(config);
        DiagSession = FormatSession(session.SessionType);
        (DiagP2, DiagP2Star) = FormatTiming(session);
        var snap = transport.SnapshotTxState();
        (DiagStMin, DiagBlockSize) = FormatFlowControl(snap);
        DiagNBs = $"{transport.FlowControlTimeout.TotalMilliseconds:0} ms";
        DiagNCr = $"{transport.ReceiveTimeout.TotalMilliseconds:0} ms";
        DiagS3Failures = session.S3FailureCount.ToString();
    }

    private void PollFlashStack()
    {
        var stack = _flash!.PeekActiveStack();
        if (stack is null)
        {
            FlashRunState = NotRunning;
            FlashTransport = NotRunning;
            FlashSession = NotRunning;
            FlashP2 = NotRunning;
            FlashP2Star = NotRunning;
            FlashStMin = NotRunning;
            FlashBlockSize = NotRunning;
            FlashNBs = NotRunning;
            FlashNCr = NotRunning;
            return;
        }

        var transport = stack.Transport;
        var session = stack.Client.Session;

        FlashRunState = "运行中";
        FlashTransport = FormatCanIds(transport.Config);
        FlashSession = FormatSession(session.SessionType);
        (FlashP2, FlashP2Star) = FormatTiming(session);
        var snap = transport.SnapshotTxState();
        (FlashStMin, FlashBlockSize) = FormatFlowControl(snap);
        FlashNBs = $"{transport.FlowControlTimeout.TotalMilliseconds:0} ms";
        FlashNCr = $"{transport.ReceiveTimeout.TotalMilliseconds:0} ms";
    }

    private static string FormatCanIds(CanIdConfig config)
    {
        var fmt = config.IsExtendedFrame ? "X8" : "X3";
        return $"0x{config.RequestId.ToString(fmt)} → 0x{config.ResponseId.ToString(fmt)}";
    }

    private static string FormatSession(byte sessionType) => sessionType switch
    {
        0x01 => "Default",
        0x02 => "Extended",
        0x03 => "Programming",
        _ => $"0x{sessionType:X2}"
    };

    private static (string P2, string P2Star) FormatTiming(UdsSession session)
    {
        var source = session.HasNegotiatedTiming ? "ECU 0x10 协商" : "本地默认";
        return ($"{session.P2Timeout} ms ({source})",
                $"{session.P2StarTimeout} ms ({source})");
    }

    private static (string StMin, string BlockSize) FormatFlowControl(IsoTpTxSnapshot snap)
    {
        if (!snap.HasReceivedFlowControl)
            return (NotReceived, NotReceived);

        var stMin = snap.StMinDelay.Ticks > 0 && snap.StMinDelay.TotalMilliseconds < 1
            ? $"{snap.StMinDelay.Ticks * 100} µs"
            : $"{snap.StMinDelay.TotalMilliseconds:0} ms";
        var bs = snap.BlockSize == 0 ? "0 (不限)" : snap.BlockSize.ToString();
        return ($"0x{snap.StMinRaw:X2} ({stMin})", bs);
    }
}
