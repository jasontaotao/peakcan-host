using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ReplayViewModel
{
    // Flow A: RangeFilter (v1.5.1 PATCH Task 2 + earlier).
    // StartTimestamp + EndTimestamp (manual properties, not [ObservableProperty] - they
    // manually push to _service on setter) + IsValidRange helper + _rangeFilterError
    // [ObservableProperty]. Sister-pattern to the 4 existing .partial.cs siblings.
    //
    // Cross-flow callers (partial-class visible):
    //   - OnTick in the service (Flow Playback.partial via _service.StartTimestamp/EndTimestamp)
    //   - SetFrames in ReplayViewModel.Loader.partial + ReplayViewModel.Playback.partial (private property reads)

    private double? _startTimestamp;

    /// <summary>
    /// Inclusive lower bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>.
    /// null = unbounded below. Setter validates against <see cref="EndTimestamp"/>;
    /// rejected updates keep the prior value and surface
    /// <see cref="RangeFilterError"/>.
    /// </summary>
    public double? StartTimestamp
    {
        get => _startTimestamp;
        set
        {
            if (!IsValidRange(value, _endTimestamp))
            {
                // Rejected: do NOT touch backing field, do NOT push to
                // service. UI binding reads back the old value via the
                // unchanged getter. RangeFilterError surfaces the reason.
                RangeFilterError = "Start must be ≤ End";
                return;
            }
            if (!EqualityComparer<double?>.Default.Equals(_startTimestamp, value))
            {
                _startTimestamp = value;
                OnPropertyChanged();
            }
            _service.StartTimestamp = value;
            RangeFilterError = null;
        }
    }

    private double? _endTimestamp;

    /// <summary>
    /// Inclusive upper bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>.
    /// null = unbounded above. Mirrors <see cref="StartTimestamp"/> validation.
    /// </summary>
    public double? EndTimestamp
    {
        get => _endTimestamp;
        set
        {
            if (!IsValidRange(_startTimestamp, value))
            {
                RangeFilterError = "Start must be ≤ End";
                return;
            }
            if (!EqualityComparer<double?>.Default.Equals(_endTimestamp, value))
            {
                _endTimestamp = value;
                OnPropertyChanged();
            }
            _service.EndTimestamp = value;
            RangeFilterError = null;
        }
    }

    /// <summary>
    /// Range constraint validator shared by <see cref="StartTimestamp"/>
    /// and <see cref="EndTimestamp"/> setters. Returns true when at least
    /// one endpoint is null, or when start &lt;= end.
    /// </summary>
    private static bool IsValidRange(double? start, double? end)
        => !(start.HasValue && end.HasValue && start > end);

    // v1.5.1 PATCH Task 2: inline error shown next to the range
    // TextBoxes when Start > End. Null when the range is valid (or
    // both bounds are null / Start ≤ End). Single shared error
    // property for both boxes — same conceptual error class.
    [ObservableProperty]
    private string? _rangeFilterError;
}
