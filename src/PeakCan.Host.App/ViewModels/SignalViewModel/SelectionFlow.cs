using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalViewModel
{
    // Flow B: Selection (v1.2.3 PATCH-2 + earlier).
    // Methods moved verbatim from SignalViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Dispose -> _drainTimer (Flow A state)
    //   - Reset -> _chartVm (DI field, main), Latest (state, main)
    //   - OnSignalSelectionChanged -> _chartVm (DI field, main)
    //   - HandlePlotCheckboxClick -> OnSignalSelectionChanged (intra-flow)
    //   - ApplyEntries -> Upsert (Flow A) + ApplyFilter (Flow D)

    /// <summary>
    /// v1.2.3 PATCH-2: the <see cref="System.Threading.Timer"/> must
    /// be disposed to release its finalizer-thread callback. The
    /// production VM is a DI singleton (Task 16 high-2 review fix
    /// said "no IDisposable because VM lives for the whole app
    /// lifetime"), but with PATCH-2 the timer holds a strong
    /// reference to <c>this</c> via the <c>OnDrainTick</c> delegate,
    /// so we promote the VM to <see cref="IDisposable"/> and have
    /// <c>AppHostBuilder</c> register it as <c>IHostedService</c> so
    /// the host disposes it on shutdown. The cost is a single
    /// <c>Dispose</c> call; the benefit is the timer no longer
    /// prevents the VM from being collected in test contexts and
    /// cleans up cleanly on app exit.
    /// </summary>
    public void Dispose()
    {
        _drainTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Clear the decoded-signal table. Called by
    /// <see cref="DbcViewModel"/> after a fresh DBC load so the grid
    /// does not display stale entries from a previous parse.
    /// Also resets the signal chart when present.
    /// </summary>
    public void Reset()
    {
        Latest.Clear();
        _chartVm?.Reset();
    }

    /// <summary>
    /// Called when a signal's IsSelected checkbox changes in the view.
    /// Adds or removes the signal from the chart.
    /// </summary>
    /// <param name="message">DBC message name.</param>
    /// <param name="signal">DBC signal name.</param>
    /// <param name="isSelected">Whether the checkbox is checked.</param>
    public void OnSignalSelectionChanged(string message, string signal, bool isSelected)
    {
        if (_chartVm is null) return;
        var key = $"{message}.{signal}";
        if (isSelected)
            _chartVm.AddSignal(key, signal);
        else
            _chartVm.RemoveSignal(key);
    }

    /// <summary>
    /// v1.2.10: routing layer for the Signal tab's per-row Plot checkbox.
    /// Takes the checkbox's UI-side IsChecked (just toggled by the click)
    /// rather than the SignalEntry's source-side IsSelected (which can be
    /// stale because DrainPending replaces the entry in Latest[i] every
    /// frame, and the row's DataContext can target the NEW entry by the
    /// time the Click handler runs). Centralised here so unit tests can
    /// verify the routing without spinning up a WPF UserControl.
    /// </summary>
    public void HandlePlotCheckboxClick(SignalEntry entry, bool isChecked)
        => OnSignalSelectionChanged(entry.Message, entry.Signal, isChecked);

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
        ApplyFilter();
    }
}