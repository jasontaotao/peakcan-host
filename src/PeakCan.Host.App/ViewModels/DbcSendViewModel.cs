using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v1.4.0 MINOR Send DBC: backing VM for the "DBC mode" panel in
/// <c>SendView.xaml</c>. Lets the user pick a DBC message, edit each
/// signal's engineering value, and send the resulting CAN frame through
/// the shared <see cref="SendService"/>.
/// <para>
/// v1.5.1 PATCH Item 2 (Periodic DBC send): adds
/// <c>StartDbcCyclicCommand</c> + <c>StopDbcCyclicCommand</c> +
/// <see cref="IsDbcCyclicRunning"/> + success / failure counters driven by
/// polling <c>ICyclicDbcSendService.SuccessCount</c> +
/// <c>ICyclicDbcSendService.FailureCount</c> via
/// <see cref="DispatcherTimer"/> at 200 ms cadence (mirror
/// <c>SendViewModel.cs:118-128</c> periodic polling pattern).
/// </para>
/// <para>
/// <b>Threading:</b> all mutations happen on the UI thread via WPF
/// bindings. <see cref="SendAsync"/> awaits the PEAK SDK on a worker
/// thread and resumes back on the UI context (CommunityToolkit's
/// <c>AsyncRelayCommand</c> uses <c>ConfigureAwait(true)</c> by default
/// in WPF projects), so <see cref="ErrorMessage"/> updates are safe to
/// bind.
/// </para>
/// <para>
/// <b>Null/empty handling:</b> the VM tolerates <see cref="DbcService.Current"/>
/// being <c>null</c> (no DBC loaded) — <see cref="DbcMessages"/> stays
/// empty and <see cref="SelectedDbcMessage"/> stays <c>null</c>. The Send
/// command is a no-op when no message is selected. Same null-tolerance
/// applies to <see cref="StartDbcCyclicCommand"/>.
/// </para>
/// </summary>
public sealed partial class DbcSendViewModel : ObservableObject
{
    private readonly DbcEncodeService _encoder;
    private readonly SendService _sendService;
    private readonly DbcService _dbcService;
    private readonly ICyclicDbcSendService _cyclicDbc;
    // v3.1.0 MINOR: real ILogger<> replaces the v3.0.9 hardcoded
    // NullLogger<DbcSendViewModel>.Instance in the rate-limit refresh
    // catch block — was a silent-logging regression (W1).
    private readonly ILogger<DbcSendViewModel> _logger;
    private readonly DispatcherTimer _cyclicPollTimer;
    // v3.0.9 PATCH: mirror of v3.0.8 SendViewModel pattern. DBC Send is
    // the high-throughput caller (one frame per encode), so operators
    // are most likely to hit the rate limit here.
    private readonly Func<long>? _getRejectedCount;

    /// <summary>All DBC messages from the loaded document. Empty if no DBC loaded.</summary>
    public ObservableCollection<Message> DbcMessages { get; } = new();

    /// <summary>One row per signal in <see cref="SelectedDbcMessage"/>. Cleared on selection change.</summary>
    public ObservableCollection<DbcSignalRowViewModel> SignalRows { get; } = new();

    [ObservableProperty]
    private Message? _selectedDbcMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>User-editable cyclic interval in milliseconds (default 100).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDbcCyclicCommand))]
    private string? _dbcCyclicIntervalText = "100";

    /// <summary>True when the cyclic DBC send is currently active (mirrors service state via poll timer).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDbcCyclicCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopDbcCyclicCommand))]
    private bool _isDbcCyclicRunning;

    /// <summary>Cyclic DBC send success count, polled from <see cref="ICyclicDbcSendService.SuccessCount"/>.</summary>
    [ObservableProperty]
    private long _dbcCyclicSuccessCount;

    /// <summary>Cyclic DBC send failure count, polled from <see cref="ICyclicDbcSendService.FailureCount"/>.</summary>
    [ObservableProperty]
    private long _dbcCyclicFailureCount;

    /// <summary>
    /// v3.0.9 PATCH: mirror of
    /// <see cref="RateLimitedSendService.RejectedFrameCount"/>. Polled
    /// every 200 ms via the existing DispatcherTimer (see <see cref="Poll"/>).
    /// Defaults to 0 (no rate-limit policy or decorator absent). UI binds
    /// this to a chip in the DBC Mode expander.
    /// </summary>
    [ObservableProperty]
    private long _rateLimitRejectedCount;

    /// <summary>
    /// v3.0.9 PATCH: chip visibility bound to
    /// <see cref="RateLimitRejectedCount"/>. Hidden when count = 0;
    /// visible when at least one DBC-encoded frame has been rejected.
    /// </summary>
    public System.Windows.Visibility RateLimitRejectedVisibility
        => RateLimitRejectedCount > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    /// <summary>
    /// v3.0.9 PATCH: re-raise PropertyChanged for the computed
    /// <see cref="RateLimitRejectedVisibility"/> whenever the underlying
    /// <see cref="RateLimitRejectedCount"/> changes.
    /// </summary>
    partial void OnRateLimitRejectedCountChanged(long value)
        => OnPropertyChanged(nameof(RateLimitRejectedVisibility));

    public DbcSendViewModel(
        DbcEncodeService encoder,
        SendService sendService,
        DbcService dbcService,
        ICyclicDbcSendService cyclicDbc,
        ILogger<DbcSendViewModel> logger,
        Func<long>? rateLimitRejectedCountProvider = null)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _dbcService = dbcService ?? throw new ArgumentNullException(nameof(dbcService));
        _cyclicDbc = cyclicDbc ?? throw new ArgumentNullException(nameof(cyclicDbc));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // v3.0.9 PATCH: optional rate-limit rejected-count provider.
        // Production DI wires this to RateLimitedSendService.RejectedFrameCount
        // when the decorator is active; null when the rate-limit policy is
        // disabled (MaxFramesPerSecond=0). Mirrors the v3.0.8 SendViewModel
        // pattern exactly.
        _getRejectedCount = rateLimitRejectedCountProvider;
        foreach (var msg in _dbcService.Current?.Messages ?? Enumerable.Empty<Message>())
        {
            DbcMessages.Add(msg);
        }

        // v1.5.1 PATCH Item 2: poll the cyclic service's state at 200 ms
        // cadence. Per CommunityToolkit.Mvvm precedent, the counters +
        // IsRunning are UI-bindable ObservableProperty. The service
        // itself exposes getter properties only (no events) to keep
        // the implementation minimal — the VM bridges via polling, same
        // pattern as SendViewModel's SuccessCount/FailureCount polling
        // (line 118-128).
        _cyclicPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _cyclicPollTimer.Tick += (s, e) => Poll();
        _cyclicPollTimer.Start();

        // v1.4.1 PATCH Item 3: subscribe to DbcLoaded so that a DBC
        // loaded AFTER the VM is constructed (e.g. user opens SendView
        // before loading DBC) repopulates DbcMessages. Per spec Decision 6:
        // match DbcViewModel precedent — do NOT implement IDisposable.
        // Both DbcService and DbcSendViewModel are app-lifetime DI
        // singletons that die together at process exit, so GC + finalizer
        // pass handles cleanup. See DbcViewModel.cs class doc for the
        // latent-footgun rationale (a previous IDisposable implementation
        // was a latent footgun per review Task 15 fix-history).
        _dbcService.DbcLoaded += OnLoaded;
    }

    /// <summary>
    /// v1.5.1 PATCH Item 2 (expanded v3.0.9): refresh the observable
    /// properties from their authoritative sources. Called every 200 ms
    /// by the <see cref="DispatcherTimer"/> in production, and directly
    /// by tests (the DispatcherTimer doesn't fire in xunit's STA-WPF
    /// test fixtures). Marked <c>internal</c> so the App.Tests assembly
    /// can invoke it via <c>[InternalsVisibleTo("PeakCan.Host.App.Tests")]</c>.
    /// </summary>
    internal void Poll()
    {
        IsDbcCyclicRunning = _cyclicDbc.IsRunning;
        DbcCyclicSuccessCount = _cyclicDbc.SuccessCount;
        DbcCyclicFailureCount = _cyclicDbc.FailureCount;
        // v3.1.0 MINOR: try/catch + [LoggerMessage] factored into the
        // shared RateLimitStatus helper (3-way DRY refactor). W1 also
        // fixed: logger was previously hardcoded to NullLogger<...>,
        // silently dropping provider exceptions.
        RateLimitRejectedCount = RateLimitStatus.Refresh(_getRejectedCount, RateLimitRejectedCount, _logger);
    }

    /// <summary>
    /// v1.4.1 PATCH Item 3: repopulate <see cref="DbcMessages"/> when a
    /// new DBC document is loaded after this VM was constructed.
    /// </summary>
    /// <remarks>
    /// <see cref="DbcService.LoadAsync"/> raises this event on its worker
    /// thread (see the threading remarks on <see cref="DbcService"/>). The handler
    /// body mutates <see cref="ObservableCollection{T}"/> instances bound
    /// to WPF <c>ItemsControl</c>s, which throws
    /// <see cref="NotSupportedException"/> on cross-thread mutation. The
    /// <see cref="DispatcherExtensions.RunOnUi"/> chokepoint marshals the
    /// body to the UI dispatcher. Mirrors the <see cref="DbcViewModel.OnLoaded"/>
    /// pattern which uses the same chokepoint.
    /// </remarks>
    private void OnLoaded(DbcDocument doc)
    {
        ((Action)(() =>
        {
            // Reset selection FIRST so OnSelectedDbcMessageChanged(null)
            // clears SignalRows via the partial method. Without this, the
            // old selection's Signal objects (now stale) would persist
            // until the user manually changes selection.
            SelectedDbcMessage = null;
            DbcMessages.Clear();
            foreach (var msg in doc.Messages)
            {
                DbcMessages.Add(msg);
            }
            // Reset prior error so a stale failure from a previous
            // message selection doesn't linger into the new document.
            ErrorMessage = null;
        })).RunOnUi();
    }

    /// <summary>
    /// Selection-change hook (CommunityToolkit.Mvvm source generator).
    /// Clears the previous signal rows and rebuilds from the new
    /// message's signal list. Null selection leaves the rows empty.
    /// <para>
    /// v1.5.1 PATCH Item 2 (Periodic DBC send): if the user changes the
    /// selected DBC message while periodic send is running, auto-stop the
    /// periodic send first. Allowing the periodic send to continue with
    /// stale SignalRows + a new Message would cause encode failures every
    /// tick (the service's Message-id auto-stop would catch this anyway,
    /// but a clean explicit stop + service call is more obvious to debug).
    /// </para>
    /// </summary>
    partial void OnSelectedDbcMessageChanged(Message? value)
    {
        if (IsDbcCyclicRunning)
        {
            _cyclicDbc.Stop();
            IsDbcCyclicRunning = false;
        }
        SignalRows.Clear();
        if (value is null) return;
        foreach (var sig in value.Signals)
        {
            SignalRows.Add(new DbcSignalRowViewModel(sig));
        }
        // StartDbcCyclic's CanExecute depends on SelectedDbcMessage.
        StartDbcCyclicCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Encode the per-signal <see cref="DbcSignalRowViewModel.Value"/>
    /// entries into a fresh <c>Dlc</c>-sized payload, build a
    /// <see cref="CanFrame"/> (Standard or Extended format depending on
    /// the message ID's bit-31 IDE flag), and dispatch it through
    /// <see cref="SendService.SendAsync"/>. Surfaces
    /// <see cref="DbcSignalEncodeException"/> as <see cref="ErrorMessage"/>
    /// so the user can correct the input; any other exception escapes
    /// (the WPF dispatcher will surface it).
    /// </summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        try
        {
            ErrorMessage = null;
            if (SelectedDbcMessage is null) return;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var row in SignalRows)
            {
                if (row.Value.HasValue) values[row.Signal.Name] = row.Value.Value;
            }
            var payload = _encoder.Encode(SelectedDbcMessage, values);
            // DBC messages use the PEAK convention: bit 31 set ⇒ Extended
            // (29-bit ID), clear ⇒ Standard (11-bit ID). The CanId ctor
            // validates the bit-width, so we must route the right format
            // to avoid ArgumentOutOfRangeException on 11-bit IDs.
            var id = SelectedDbcMessage.Id;
            var isExtended = (id & 0x80000000u) != 0u;
            var raw = isExtended ? (id & 0x1FFFFFFFu) : (id & 0x7FFu);
            var canId = new CanId(raw, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
            var frame = new CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default);
            await _sendService.SendAsync(frame).ConfigureAwait(true);
        }
        catch (DbcSignalEncodeException ex)
        {
            // Range / not-found / multiplexor / configuration errors all
            // derive from this base. The exception message already
            // identifies the offending signal and (when applicable) the
            // valid range — show it directly so the user can correct the
            // input without consulting logs.
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// v1.5.1 PATCH Item 2: start periodic DBC transmission on the
    /// selected message at <see cref="DbcCyclicIntervalText"/> ms.
    /// The frame provider supplies the current <see cref="SelectedDbcMessage"/>
    /// + per-signal values, so user edits to the SignalRows DataGrid
    /// flow into the periodic send path on the next tick (Decision 8).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartDbcCyclic))]
    private void StartDbcCyclic()
    {
        if (SelectedDbcMessage is null) return;
        // v1.5.1 PATCH Item 2: interval is MILLISECONDS (UI label says so),
        // not a TimeSpan string. TimeSpan.TryParse("100") returns 100 days,
        // which would silently make the periodic send a 100-day timer.
        // Mirror SendViewModel.cs:279-282 pattern: int.TryParse + bounds 1..60000.
        if (!int.TryParse(DbcCyclicIntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            || ms < 1 || ms > 60_000)
        {
            ErrorMessage = $"Invalid interval: '{DbcCyclicIntervalText}' (must be 1..60000 ms)";
            return;
        }
        var interval = TimeSpan.FromMilliseconds(ms);
        _cyclicDbc.Start(
            () => (SelectedDbcMessage!, BuildCurrentSignalValues()),
            interval);
        IsDbcCyclicRunning = true;
    }

    /// <summary>v1.5.1 PATCH Item 2: stop the periodic DBC transmission.</summary>
    [RelayCommand(CanExecute = nameof(CanStopDbcCyclic))]
    private void StopDbcCyclic()
    {
        _cyclicDbc.Stop();
        IsDbcCyclicRunning = false;
    }

    private bool CanStartDbcCyclic() =>
        SelectedDbcMessage is not null
        && !IsDbcCyclicRunning
        && int.TryParse(DbcCyclicIntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
        && ms >= 1 && ms <= 60_000;

    private bool CanStopDbcCyclic() => IsDbcCyclicRunning;

    /// <summary>
    /// v1.5.1 PATCH Item 2: capture the current per-signal values into
    /// a fresh dictionary snapshot. The Func&lt;...&gt; provided to
    /// <see cref="CyclicDbcSendService.Start"/> invokes this on each
    /// tick, so user edits to the SignalRows DataGrid flow into the
    /// periodic encode path naturally.
    /// </summary>
    private Dictionary<string, double> BuildCurrentSignalValues()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in SignalRows)
        {
            if (row.Value.HasValue) values[row.Signal.Name] = row.Value.Value;
        }
        return values;
    }
}

/// <summary>
/// Per-signal row VM bound to a single <see cref="Signal"/> in the
/// <c>DbcSendViewModel.SignalRows</c> DataGrid. <see cref="Value"/> is
/// nullable so a blank cell means "do not encode this signal" (the
/// encoder treats missing values as 0-bits on the wire).
/// </summary>
public sealed partial class DbcSignalRowViewModel : ObservableObject
{
    public Signal Signal { get; }

    [ObservableProperty]
    private double? _value;

    public DbcSignalRowViewModel(Signal signal)
    {
        Signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    /// <summary>Human-readable column for the "Signal" header.</summary>
    public string DisplayName =>
        $"{Signal.Name} ({Signal.Length} bit, [{Signal.Min:F2}, {Signal.Max:F2}] {Signal.Unit})";

    /// <summary>DBC value-type name (Unsigned / Signed / Float / Double) for the "Type" column.</summary>
    public string ValueType => Signal.ValueType.ToString();
}
