using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Windows;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// View-model for the manual-send form (<c>SendView.xaml</c>). Two text
/// inputs (ID + Data) and two checkboxes (Extended / CAN FD) feed a single
/// <see cref="RelayCommand"/> that builds a <see cref="CanFrame"/>, hands
/// it to <see cref="SendService"/>, and surfaces the outcome in
/// <see cref="Status"/>.
/// <para>
/// The command is intentionally <c>async</c> because
/// <see cref="SendService.SendAsync"/> awaits the PEAK SDK on a worker
/// thread; exceptions from that path are caught and turned into a
/// "FAIL: …" status rather than propagating out of the command (which
/// would surface as an unhandled exception in the WPF dispatcher).
/// </para>
/// <para>
/// v1.2.12 PATCH Item 6: also implements <see cref="IHostedService"/>
/// (no-op Start/Stop) so <c>AppHostBuilder</c> can register it with
/// <c>AddHostedService</c> and the host will call <see cref="Dispose"/>
/// on shutdown. Without this, the poll timer would keep the VM alive
/// across STA-WPF xunit test fixtures and leak in production after
/// the shell navigates away.
/// </para>
/// </summary>
public sealed partial class SendViewModel : ObservableObject, IHostedService, IDisposable
{
    private readonly SendService _svc;
    private readonly ICyclicSendService _cyclic;
    private readonly SendFrameLibrary? _libraryService;
    private readonly ILogger<SendViewModel> _logger;
    // A4 orphan PATCH (v3.0.8): optional provider that returns the
    // current rate-limit rejected frame count. Production DI wires this
    // to RateLimitedSendService.RejectedFrameCount when the decorator
    // is active; tests pass a controllable lambda; the default null
    // means the UI stays at 0 (no rate-limit policy active).
    private readonly Func<long>? _getRejectedCount;
    // v1.2.11 PATCH review fix (HIGH): hold a reference to the poll timer
    // so Dispose can stop it. Without Dispose the timer ticks for the
    // VM lifetime and (via Tick closure over _cyclic) keeps the VM alive
    // even after the shell navigates away.
    private readonly System.Windows.Threading.DispatcherTimer _pollTimer;

    [ObservableProperty]
    private string _idText = "100";

    [ObservableProperty]
    private bool _isExtended;

    [ObservableProperty]
    private bool _isFd;

    // v1.2.11 PATCH Item 4: frame-level flags exposed on the Send form.
    [ObservableProperty]
    private bool _isRtr;

    [ObservableProperty]
    private bool _isBitRateSwitch;

    [ObservableProperty]
    private bool _isErrorStateIndicator;

    [ObservableProperty]
    private string _dataText = "DEADBEEF";

    [ObservableProperty]
    private string _status = string.Empty;

    // v1.2.11 PATCH Item 3: cyclic-send state surfaced to the SendView form.
    [ObservableProperty]
    private string _cyclicIntervalText = "100";

    [ObservableProperty]
    private bool _isCyclicRunning;

    [ObservableProperty]
    private long _cyclicSuccessCount;

    [ObservableProperty]
    private long _cyclicFailureCount;

    // A4 orphan PATCH (v3.0.8): mirror of
    // RateLimitedSendService.RejectedFrameCount, polled every 200 ms.
    // Defaults to 0 (no rate-limit policy or decorator absent). UI binds
    // this to a small "rate limit rejected: N" chip in the single-shot
    // section so operators can see when their burst exceeds the cap.
    [ObservableProperty]
    private long _rateLimitRejectedCount;

    /// <summary>
    /// A4 orphan PATCH (v3.0.8): chip visibility bound to
    /// <see cref="RateLimitRejectedCount"/>. Hidden when the counter is
    /// 0 (no rejections or rate-limit decorator absent); visible when
    /// at least one frame has been rejected.
    /// </summary>
    public System.Windows.Visibility RateLimitRejectedVisibility
        => RateLimitRejectedCount > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;


    // v1.2.11 PATCH Item 5 UI: library list bound to the SendView DataGrid.
    [ObservableProperty]
    private ObservableCollection<SendFrameLibrary.SavedFrame> _library = new();

    [ObservableProperty]
    private SendFrameLibrary.SavedFrame? _selectedLibraryFrame;

    // v1.4.0 MINOR Send DBC: the DBC-mode sub-panel. Resolved from DI
    // (AppHostBuilder registers DbcSendViewModel as a singleton); the
    // SendView XAML binds its child controls to this property so the
    // message dropdown, signal DataGrid, and Send button stay in sync
    // with the rest of the Send tab.
    [ObservableProperty]
    private DbcSendViewModel? _dbcSend;

    // v2.1.0 MINOR: multi-frame send VM (singleton in DI). The
    // Window itself is NOT DI-registered because WPF Window
    // construction requires STA + a live Application — instantiating
    // it via the DI container (which runs in any thread) throws.
    // Instead we lazy-create the window in OpenMultiFrameSend.
    private readonly MultiFrameSendViewModel? _multiFrameVm;

    public SendViewModel(SendService svc, ILogger<SendViewModel> logger, ICyclicSendService cyclic, SendFrameLibrary? library, DbcSendViewModel? dbcSend = null, MultiFrameSendViewModel? multiFrameVm = null, Func<long>? rateLimitRejectedCountProvider = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cyclic = cyclic ?? throw new ArgumentNullException(nameof(cyclic));
        // Library may be null in unit tests until Task 8 wires it up;
        // production DI provides a singleton instance.
        _libraryService = library;
        // DBC sub-panel may be null in unit tests that pre-date
        // DbcSendViewModel registration; production DI always provides it.
        _dbcSend = dbcSend;
        // Multi-frame VM may be null in unit tests that pre-date
        // v2.1.0; production DI always provides it.
        _multiFrameVm = multiFrameVm;
        // A4 orphan PATCH: rate-limit reject counter provider. Null in
        // test scenarios that pre-date the rate-limit decorator, or in
        // production when the decorator is disabled (MaxFramesPerSecond=0
        // returns the inner CoreSendService directly, bypassing the
        // decorator — AppHostBuilder detects this case and wires null).
        _getRejectedCount = rateLimitRejectedCountProvider;

        // v1.2.11 PATCH Item 3: poll the cyclic service every 200 ms so
        // the UI reflects IsRunning / SuccessCount / FailureCount without
        // a separate event. v1.2.12 PATCH Item 10 split the mixed
        // SendCount into Success + Failure so the UI shows the two
        // outcomes separately.
        // DispatcherTimer ctor doesn't require WPF Application; in test
        // context (no Application) the Tick simply never fires — fine.
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
    }

    /// <summary>
    /// v1.2.11 PATCH Item 3 (expanded A4 orphan): refresh the
    /// observable properties from their authoritative sources. Called
    /// every 200 ms by the <see cref="DispatcherTimer"/> in production,
    /// and directly by tests (the DispatcherTimer doesn't fire in
    /// xunit's STA-WPF test fixtures). Marked <c>internal</c> so the
    /// App.Tests assembly can invoke it via
    /// <c>[InternalsVisibleTo("PeakCan.Host.App.Tests")]</c>.
    /// </summary>
    internal void Poll()
    {
        IsCyclicRunning = _cyclic.IsRunning;
        CyclicSuccessCount = _cyclic.SuccessCount;
        CyclicFailureCount = _cyclic.FailureCount;
        // A4 orphan PATCH: refresh the rate-limit reject counter.
        // The [ObservableProperty] source-generated setter on
        // RateLimitRejectedCount already short-circuits when the new
        // value equals the old (EqualityComparer<long>.Default.Equals),
        // so we don't need an explicit idempotent guard here — direct
        // assignment is safe and avoids a redundant comparison.
        // v3.1.0 MINOR: try/catch + [LoggerMessage] factored into the
        // shared RateLimitStatus helper (3-way DRY refactor).
        RateLimitRejectedCount = RateLimitStatus.Refresh(_getRejectedCount, RateLimitRejectedCount, _logger);
    }


    // v1.2.12 PATCH Item 6: IHostedService no-op implementations. See
    // RecordViewModel for rationale — the VM is a passive sink, the
    // DispatcherTimer starts in the ctor, and these exist only so the
    // host can call Dispose on shutdown.
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;


    /// <summary>
    /// Parse a hex string into bytes. Accepts optional spaces and dashes
    /// as separators (e.g. <c>"DE AD-BE EF"</c>) and pads odd-length input
    /// with a leading zero (e.g. <c>"ABC"</c> → <c>{0x0A, 0xBC}</c>).
    /// </summary>
    /// <exception cref="FormatException">A non-hex character survived the separator strip, OR the input was empty / separators-only (no hex digits to parse).</exception>
    private static byte[] ParseHex(string s)
    {
        var stripped = s.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("-", string.Empty, StringComparison.Ordinal);
        if (stripped.Length == 0)
        {
            // Footgun guard: clicking Send with an empty Data field (or
            // spaces/dashes only) previously produced a silent DLC=0
            // transmission. Reject loudly so the user fixes the input.
            throw new FormatException("Hex data is empty (only separators or no input).");
        }
        if ((stripped.Length & 1) == 1)
        {
            stripped = "0" + stripped;
        }
        var bytes = new byte[stripped.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(stripped.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return bytes;
    }

    /// <summary>
    /// v1.2.11 PATCH Item 3: central flag builder shared by
    /// <see cref="SendAsync"/> and <see cref="StartCyclic"/>. Single source
    /// of truth so the manual-send path and cyclic path produce identical
    /// bitmasks for the same checkbox state.
    /// </summary>
    private FrameFlags BuildFlags()
    {
        var flags = FrameFlags.None;
        if (IsFd) flags |= FrameFlags.Fd;
        if (IsRtr) flags |= FrameFlags.Rtr;
        if (IsBitRateSwitch) flags |= FrameFlags.BitRateSwitch;
        if (IsErrorStateIndicator) flags |= FrameFlags.ErrorStateIndicator;
        return flags;
    }





    // === Flow D methods moved to SendViewModel/LifecycleFlow.cs (W6 Task 1) ===
    // === Flow B methods moved to SendViewModel/CyclicFlow.cs (W6 Task 2) ===
    // === Flow C methods moved to SendViewModel/LibraryFlow.cs (W6 Task 3) ===
    // === Flow A methods moved to SendViewModel/FrameSendFlow.cs (W6 Task 4) ===
}
