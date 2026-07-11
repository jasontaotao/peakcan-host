using System.Windows;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class MultiFrameSendViewModel
{
    // Flow D: DbcIntegration (v3.0.9 PATCH + DBC load callback).
    // Methods moved verbatim from MultiFrameSendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - OnRateLimitRejectedCountChanged -> OnPropertyChanged (main helper)
    //   - OnDbcLoaded -> AvailableDbcMessages (state, main)
    //   - OnDbcLoaded -> RunOnUi (DispatcherExtensions helper)

    /// <summary>
    /// v3.0.9 PATCH: re-raise PropertyChanged for the computed
    /// <see cref="RateLimitRejectedVisibility"/> whenever the underlying
    /// <see cref="RateLimitRejectedCount"/> changes.
    /// </summary>
    partial void OnRateLimitRejectedCountChanged(long value)
        => OnPropertyChanged(nameof(RateLimitRejectedVisibility));

    private void OnDbcLoaded(DbcDocument doc)
    {
        // DbcLoaded fires on a worker thread (DbcService.LoadAsync);
        // ObservableCollection mutation must happen on the UI
        // dispatcher. RunOnUi pattern matches DbcViewModel /
        // DbcSendViewModel.
        ((Action)(() =>
        {
            AvailableDbcMessages.Clear();
            foreach (var msg in doc.Messages)
                AvailableDbcMessages.Add(msg);
        })).RunOnUi();
    }
}