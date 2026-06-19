using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Backing view model for the Signal tab (<c>SignalView.xaml</c>). Owns
/// the <see cref="Latest"/> collection bound to the DataGrid and an
/// upsert path keyed by <c>(Message, Signal)</c> so the grid stays
/// stable as frames stream in.
/// <para>
/// <b>Decoding:</b> every signal inside a matching DBC <see cref="Message"/>
/// is decoded via <see cref="SignalDecoder.Decode"/>. Multiplexor and
/// multiplexed signals are deliberately skipped (v1.1 per plan §2).
/// </para>
/// <para>
/// <b>Event wiring:</b> the shell registers this VM as a DI singleton
/// that lives for the whole app — there are no subscriptions to
/// unsubscribe. <b>Both this VM and its callers die together at
/// process exit</b> (same rationale as <see cref="DbcViewModel"/> per
/// the Task 15 HIGH-2 review fix).
/// </para>
/// <para>
/// <b>Concurrency model:</b> <see cref="ApplyFrame"/> is called from
/// the <see cref="Services.TraceService"/> SDK read thread. The decode
/// path runs on the calling thread (pure / stateless), but the
/// resulting <see cref="Latest"/> mutations MUST be marshalled to the
/// WPF UI thread because the <c>ItemsControl</c> binding rejects
/// cross-thread <c>CollectionChanged</c> notifications with
/// <c>NotSupportedException</c> (Task 15 review pattern; mirrors
/// <see cref="DbcViewModel.OnLoaded"/>). The CPU-light decode stays on
/// the calling thread; the binding-visible upsert is queued to the
/// dispatcher via <see cref="System.Windows.Threading.DispatcherOperation"/>
/// (<c>InvokeAsync</c> — non-blocking so the SDK read thread is not
/// slowed at ~8 000 fps). In test contexts (no <c>Application</c>) the
/// dispatcher is null and the upsert runs inline so the test can
/// observe the post-state.
/// </para>
/// </summary>
public sealed class SignalViewModel : ObservableObject
{
    /// <summary>
    /// Backing store of decoded-signal rows. Mutated only on the WPF UI
    /// thread (via <see cref="ApplyFrame"/>'s dispatcher hop); reads
    /// from the UI thread are direct, reads from background threads
    /// are safe because the WPF DataGrid binding marshals reads to the
    /// UI thread automatically.
    /// </summary>
    public ObservableCollection<SignalEntry> Latest { get; } = new();

    /// <summary>
    /// Decode the non-multiplexed signals in <paramref name="msg"/>
    /// against <paramref name="frame"/> and upsert one row per signal
    /// into <see cref="Latest"/>. The key is <c>(Message, Signal)</c>;
    /// a fresh frame with the same key replaces the existing row
    /// (in-place via indexer assignment) so the DataGrid does not
    /// thrash the virtualization recycling pass.
    /// </summary>
    public void ApplyFrame(CanFrame frame, Message msg)
    {
        // Decode on the calling thread — the decode path is pure (no
        // shared state) and SignalDecoder is cheap. Build a local
        // list of new entries; the collection mutation hops to the
        // dispatcher below so the WPF binding sees a UI-thread
        // CollectionChanged.
        var span = frame.Data.Span;
        var pending = new List<SignalEntry>(msg.Signals.Count);
        foreach (var sig in msg.Signals)
        {
            // v1.1 deferred: multiplexor + multiplexed signals need
            // mux-value extraction before the row is meaningful.
            if (sig.IsMultiplexor || sig.IsMultiplexed) continue;

            var raw = SignalDecoder.Decode(span, sig);
            pending.Add(new SignalEntry
            {
                Message = msg.Name,
                Signal = sig.Name,
                Raw = $"0x{raw.ToString("F0", CultureInfo.InvariantCulture)}",
                Physical = raw.ToString("0.###", CultureInfo.InvariantCulture),
                Unit = sig.Unit,
            });
        }
        if (pending.Count == 0) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // Production: SDK read thread → hop to UI thread. Fire and
            // forget via InvokeAsync so the SDK thread is not blocked
            // at ~8 kfps. The Operation is not awaited (caller does
            // not need ordering or completion).
            dispatcher.InvokeAsync(() => ApplyEntries(pending));
            return;
        }
        // Inline path: either we're already on the UI thread, or no
        // Application is running (test context). Tests that touch
        // Latest directly observe the post-state here.
        ApplyEntries(pending);
    }

    /// <summary>
    /// Clear the decoded-signal table. Called by
    /// <see cref="DbcViewModel"/> after a fresh DBC load so the grid
    /// does not display stale entries from a previous parse.
    /// </summary>
    public void Reset() => Latest.Clear();

    // Upsert by (Message, Signal). The grid is small (one row per signal
    // per loaded message — typically < 100 rows) so a linear scan is
    // fine. If a future task pushes this to thousands of signals,
    // promote to a Dictionary<string,int> index alongside the
    // ObservableCollection (mirror the TraceViewModel FIFO-trim pattern).
    private void Upsert(SignalEntry entry)
    {
        for (var i = 0; i < Latest.Count; i++)
        {
            if (Latest[i].Message == entry.Message && Latest[i].Signal == entry.Signal)
            {
                Latest[i] = entry;
                return;
            }
        }
        Latest.Add(entry);
    }

    // Bulk apply a batch of decoded entries. Always invoked on the UI
    // thread (either via InvokeAsync from ApplyFrame, or inline when
    // the dispatcher is null in test context). Single insertion pass
    // per batch — the per-entry Upsert is O(N) so the total is O(N*M)
    // where N=batch size, M=current row count. Acceptable for typical
    // batch sizes (1 frame per ApplyFrame call from the SDK thread;
    // < 50 signals per DBC message).
    private void ApplyEntries(IReadOnlyList<SignalEntry> entries)
    {
        foreach (var e in entries) Upsert(e);
    }
}