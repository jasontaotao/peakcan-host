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
/// <b>Concurrency model:</b> <see cref="ApplyFrame"/> mutates
/// <see cref="Latest"/> directly on the calling thread. The decode
/// path is pure (no shared state) and the upsert is a linear scan of a
/// typically-small collection (one row per signal per loaded message,
/// &lt;100 rows in practice). WPF's <c>ItemsControl</c> binding
/// internally marshals <c>CollectionChanged</c> notifications onto the
/// dispatcher when an active visual tree is attached, so the DataGrid
/// stays in sync without us marshalling explicitly. Tests that read
/// <see cref="Latest"/> directly (no binding) observe the post-state
/// inline. If a future task surfaces cross-thread
/// <c>NotSupportedException</c> from the binding, the fix is a
/// dispatcher marshalling layer around <see cref="Upsert"/>, not
/// around <see cref="ApplyFrame"/>.
/// </para>
/// </summary>
public sealed class SignalViewModel : ObservableObject
{
    /// <summary>
    /// Backing store of decoded-signal rows. Mutated from the calling
    /// thread of <see cref="ApplyFrame"/>; reads from any thread are
    /// safe because the WPF DataGrid binding marshals reads to the UI
    /// thread.
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
        // Decode all signals on the calling thread — the decode path is
        // pure (no shared state) and SignalDecoder is cheap. Mutate
        // Latest directly on the calling thread. The WPF DataGrid
        // binding internally marshals CollectionChanged notifications
        // onto the dispatcher when an active visual tree exists;
        // tests that touch the collection directly (no binding) just
        // observe the post-state inline.
        var span = frame.Data.Span;
        foreach (var sig in msg.Signals)
        {
            // v1.1 deferred: multiplexor + multiplexed signals need
            // mux-value extraction before the row is meaningful.
            if (sig.IsMultiplexor || sig.IsMultiplexed) continue;

            var raw = SignalDecoder.Decode(span, sig);
            Upsert(new SignalEntry
            {
                Message = msg.Name,
                Signal = sig.Name,
                Raw = $"0x{raw.ToString("F0", CultureInfo.InvariantCulture)}",
                Physical = raw.ToString("0.###", CultureInfo.InvariantCulture),
                Unit = sig.Unit,
            });
        }
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
}