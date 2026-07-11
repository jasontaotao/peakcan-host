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

    /// <summary>
    /// Raised by <see cref="OnRateLimitRejectedCountChanged"/> whenever
    /// <see cref="RateLimitRejectedCount"/> changes so WPF re-evaluates
    /// <see cref="RateLimitRejectedVisibility"/>. Without this hook the
    /// Visibility binding would not refresh because the underlying
    /// property change notification is for the long, not the computed
    /// Visibility.
    /// </summary>
    partial void OnRateLimitRejectedCountChanged(long value)
        => OnPropertyChanged(nameof(RateLimitRejectedVisibility));

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

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!uint.TryParse(IdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            Status = $"Invalid ID: {IdText}";
            LogInvalidId(_logger, IdText);
            return;
        }
        // Pre-validate ID against the chosen frame format. CanId ctor
        // throws ArgumentOutOfRangeException for out-of-range raw values;
        // we surface a friendly status here instead of letting the
        // SDK exception path handle it.
        var maxId = IsExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (raw > maxId)
        {
            Status = $"ID 0x{raw:X} exceeds max for {(IsExtended ? "Extended (29-bit)" : "Standard (11-bit)")} (max 0x{maxId:X})";
            LogInvalidId(_logger, IdText);
            return;
        }
        byte[] bytes;
        try
        {
            bytes = ParseHex(DataText);
        }
        catch (FormatException ex)
        {
            // Defensive: ParseHex only throws on a non-hex character that
            // survived the strip-separator step, OR on an all-separator
            // input that strips down to empty (footgun: user clicks Send
            // after clearing the data field). Treat both as a user input
            // error, not a bug.
            Status = $"Invalid data: {ex.Message}";
            LogInvalidData(_logger, DataText, ex);
            return;
        }
        var canId = new CanId(raw, IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        // v1.2.11 PATCH Item 4: RTR + FD is not a valid CAN frame per the
        // ISO 11898-1 spec (RTR applies to classic CAN only). Reject loudly
        // so the user fixes the input rather than seeing a silent zero-byte
        // classic frame go out.
        if (IsRtr && IsFd)
        {
            Status = "RTR is not valid for CAN FD (classic CAN only)";
            LogInvalidId(_logger, "RTR+FD");
            return;
        }
        var flags = BuildFlags();
        var frame = new CanFrame(canId, bytes, flags, ChannelId.None, default);
        try
        {
            var r = await _svc.SendAsync(frame).ConfigureAwait(true);
            Status = r.IsSuccess
                ? $"Sent {bytes.Length} bytes @ 0x{canId}"
                : $"FAIL: {r.Error!.Code} {r.Error.Message}";
            if (r.IsSuccess)
            {
                LogSendOk(_logger, canId, bytes.Length);
            }
            else
            {
                LogSendFailed(_logger, canId, r.Error!.Code, r.Error.Message);
            }
        }
        catch (Exception ex)
        {
            // Never let an SDK / channel exception escape the command —
            // the WPF dispatcher would surface it as an unhandled
            // exception and crash the app on a non-issue.
            Status = $"FAIL: {ex.Message}";
            LogSendThrew(_logger, canId, ex);
        }
    }

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

    // v1.2.11 PATCH Item 3: cyclic-send commands exposed to SendView.xaml.

    [RelayCommand]
    private void StartCyclic()
    {
        if (!uint.TryParse(IdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            Status = $"Invalid ID: {IdText}";
            return;
        }
        if (!int.TryParse(CyclicIntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            || ms < 1 || ms > 60_000)
        {
            Status = $"Invalid interval: {CyclicIntervalText} (must be 1..60000 ms)";
            return;
        }
        if (IsRtr && IsFd)
        {
            Status = "RTR is not valid for CAN FD (classic CAN only)";
            return;
        }
        var bytes = ParseHex(DataText);
        var canId = new CanId(raw, IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var frame = new CanFrame(canId, bytes, BuildFlags(), ChannelId.None, default);
        _cyclic.Start(frame, TimeSpan.FromMilliseconds(ms));
        IsCyclicRunning = _cyclic.IsRunning;
        Status = $"Cyclic started: every {ms} ms";
    }

    [RelayCommand]
    private void StopCyclic()
    {
        _cyclic.Stop();
        IsCyclicRunning = _cyclic.IsRunning;
        Status = $"Cyclic stopped ({CyclicSuccessCount} ok / {CyclicFailureCount} fail)";
    }

    // v1.2.11 PATCH Item 5 UI: library commands bound to the SendView Expander.

    [RelayCommand]
    private void RefreshLibrary()
    {
        Library.Clear();
        if (_libraryService is null) return;
        foreach (var f in _libraryService.Load()) Library.Add(f);
    }

    [RelayCommand]
    private void SaveCurrentToLibrary(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "Frame name is required";
            return;
        }
        if (!uint.TryParse(IdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            Status = $"Invalid ID: {IdText}";
            return;
        }
        if (_libraryService is null)
        {
            Status = "Library unavailable";
            return;
        }
        byte[] bytes;
        try { bytes = ParseHex(DataText); }
        catch (FormatException ex)
        {
            Status = $"Invalid data: {ex.Message}";
            return;
        }
        var saved = new SendFrameLibrary.SavedFrame(
            name, raw, IsExtended, IsFd, IsRtr, IsBitRateSwitch,
            Convert.ToHexString(bytes), DateTimeOffset.UtcNow);
        // v1.2.12 PATCH Item 1: route through the atomic Add so concurrent
        // Save calls (e.g. double-clicked button) don't drop each other's
        // read-modify-write. Catch IO / JSON exceptions and surface as a
        // FAIL status rather than letting them escape the WPF dispatcher.
        try
        {
            _libraryService.Add(saved);
            RefreshLibrary();
            Status = $"Saved '{name}' to library ({_libraryService.Count} frames).";
        }
        catch (Exception ex)
        {
            Status = $"FAIL: Save '{name}' to library: {ex.Message}";
            LogSaveToLibraryFailed(_logger, ex, name);
        }
    }

    [RelayCommand]
    private void LoadFromLibrary(SendFrameLibrary.SavedFrame? frame)
    {
        if (frame is null) return;
        IdText = frame.RawId.ToString("X", CultureInfo.InvariantCulture);
        IsExtended = frame.IsExtended;
        IsFd = frame.IsFd;
        IsRtr = frame.IsRtr;
        IsBitRateSwitch = frame.BitRateSwitch;
        DataText = frame.DataHex;
        Status = $"Loaded '{frame.Name}'";
    }

    [RelayCommand]
    private void DeleteFromLibrary(SendFrameLibrary.SavedFrame? frame)
    {
        if (frame is null || _libraryService is null) return;
        // v1.2.12 PATCH Item 1: route through the atomic Remove so concurrent
        // Delete calls don't drop each other's read-modify-write. Report
        // a friendly status when the frame was already gone (idempotent
        // delete), and surface IO failures as FAIL.
        try
        {
            if (_libraryService.Remove(frame.Name))
            {
                RefreshLibrary();
                Status = $"Removed '{frame.Name}' from library.";
            }
            else
            {
                Status = $"'{frame.Name}' not found in library (already removed?).";
            }
        }
        catch (Exception ex)
        {
            Status = $"FAIL: Remove '{frame.Name}': {ex.Message}";
            LogDeleteFromLibraryFailed(_logger, ex, frame.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Send rejected: invalid ID hex '{Input}'")]
    private static partial void LogInvalidId(ILogger logger, string input);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Send rejected: invalid data hex '{Input}'")]
    private static partial void LogInvalidData(ILogger logger, string input, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent CAN frame {CanId} ({Length} bytes)")]
    private static partial void LogSendOk(ILogger logger, CanId canId, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Send failed for {CanId}: {Code} {Message}")]
    private static partial void LogSendFailed(ILogger logger, CanId canId, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Send threw for {CanId}")]
    private static partial void LogSendThrew(ILogger logger, CanId canId, Exception ex);

    // v1.2.12 PATCH Item 1: log IO / JSON failures from the atomic library
    // Add/Remove path. The Status string is user-facing; these messages
    // are the operator-facing diagnostics that survive a UI crash.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Save '{Name}' to library failed")]
    private static partial void LogSaveToLibraryFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Delete '{Name}' from library failed")]
    private static partial void LogDeleteFromLibraryFailed(ILogger logger, Exception ex, string name);

    // v2.1.0 MINOR: open the multi-frame send window (non-modal).
    // The window is lazy-created on first call: WPF Window construction
    // requires an STA thread + a live Application, so we can't
    // resolve a Window from DI at container-build time. The VM is
    // DI-resolved (singleton) but the Window itself is owned by
    // SendViewModel and kept alive for the SendView's lifetime.
    private MultiFrameSendWindow? _openMultiFrameWindow;

    [RelayCommand]
    private void OpenMultiFrameSend()
    {
        if (_multiFrameVm is null)
        {
            Status = "Multi-frame window unavailable";
            return;
        }
        if (_openMultiFrameWindow is { } existing && existing.IsVisible)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _openMultiFrameWindow = new MultiFrameSendWindow(_multiFrameVm);
        if (Application.Current?.MainWindow is { } owner && owner != _openMultiFrameWindow)
            _openMultiFrameWindow.Owner = owner;
        // v3.9.2 PATCH L3: mirror the v3.9.1 PATCH B1 fix
        // (AppShellViewModel._traceViewerView.Closed reset) so the next
        // OpenMultiFrameSend click takes the fresh-window path instead
        // of stomp-and-leak on a closed instance.
        _openMultiFrameWindow.Closed += (_, _) => _openMultiFrameWindow = null;
        _openMultiFrameWindow.Show();
        Status = "Multi-frame send window opened";
    }
    // === Flow D methods moved to SendViewModel/LifecycleFlow.cs (W6 Task 1) ===
}
