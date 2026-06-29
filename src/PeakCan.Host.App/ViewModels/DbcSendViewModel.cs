using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// command is a no-op when no message is selected.
/// </para>
/// </summary>
public sealed partial class DbcSendViewModel : ObservableObject
{
    private readonly DbcEncodeService _encoder;
    private readonly SendService _sendService;
    private readonly DbcService _dbcService;

    /// <summary>All DBC messages from the loaded document. Empty if no DBC loaded.</summary>
    public ObservableCollection<Message> DbcMessages { get; } = new();

    /// <summary>One row per signal in <see cref="SelectedDbcMessage"/>. Cleared on selection change.</summary>
    public ObservableCollection<DbcSignalRowViewModel> SignalRows { get; } = new();

    [ObservableProperty]
    private Message? _selectedDbcMessage;

    [ObservableProperty]
    private string? _errorMessage;

    public DbcSendViewModel(DbcEncodeService encoder, SendService sendService, DbcService dbcService)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _dbcService = dbcService ?? throw new ArgumentNullException(nameof(dbcService));
        foreach (var msg in _dbcService.Current?.Messages ?? Enumerable.Empty<Message>())
        {
            DbcMessages.Add(msg);
        }

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
    /// v1.4.1 PATCH Item 3: repopulate <see cref="DbcMessages"/> when a
    /// new DBC document is loaded after this VM was constructed.
    /// </summary>
    /// <remarks>
    /// <see cref="DbcService.LoadAsync"/> raises this event on its worker
    /// thread (see <c>DbcService.cs:17-22</c> class doc). The handler
    /// body mutates <see cref="ObservableCollection{T}"/> instances bound
    /// to WPF <c>ItemsControl</c>s, which throws
    /// <see cref="NotSupportedException"/> on cross-thread mutation. The
    /// <see cref="DispatcherExtensions.RunOnUi"/> chokepoint marshals the
    /// body to the UI dispatcher. Mirrors the <see cref="DbcViewModel.OnLoaded"/>
    /// pattern (lines 112-147) which uses the same chokepoint.
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
    /// </summary>
    partial void OnSelectedDbcMessageChanged(Message? value)
    {
        SignalRows.Clear();
        if (value is null) return;
        foreach (var sig in value.Signals)
        {
            SignalRows.Add(new DbcSignalRowViewModel(sig));
        }
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
