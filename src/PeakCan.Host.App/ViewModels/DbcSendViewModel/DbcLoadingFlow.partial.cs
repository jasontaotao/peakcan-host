using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcSendViewModel
{
    /// <summary>
    /// v3.0.9 PATCH: re-raise PropertyChanged for the computed
    /// <see cref="RateLimitRejectedVisibility"/> whenever the underlying
    /// <see cref="RateLimitRejectedCount"/> changes.
    /// </summary>
    partial void OnRateLimitRejectedCountChanged(long value)
        => OnPropertyChanged(nameof(RateLimitRejectedVisibility));

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
}