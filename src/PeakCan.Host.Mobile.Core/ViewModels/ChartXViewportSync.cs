namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>A finite, increasing one-dimensional axis range.</summary>
public readonly record struct ChartAxisRange(double Minimum, double Maximum);

/// <summary>A chart axis adapter that can read and apply the shared X range.</summary>
public interface IChartXAxisViewport
{
    /// <summary>Reads the current axis range when both limits are set.</summary>
    bool TryGetRange(out ChartAxisRange range);

    /// <summary>Applies a range to the axis.</summary>
    void SetRange(ChartAxisRange range);
}

/// <summary>Keeps the X viewport of every trace-chart subplot synchronized.</summary>
public sealed class ChartXViewportSync
{
    private readonly List<IChartXAxisViewport> _axes = [];
    private ChartAxisRange? _range;
    private bool _syncing;

    public ChartAxisRange? Range => _range;

    /// <summary>Replaces tracked axes; any stored range is applied to the new axes.</summary>
    public void Attach(IEnumerable<IChartXAxisViewport> axes)
    {
        ArgumentNullException.ThrowIfNull(axes);

        _axes.Clear();
        _axes.AddRange(axes);
        if (_range is { } range && !_syncing)
            Apply(range);
    }

    /// <summary>Stops tracking axes while retaining the current viewport.</summary>
    public void Clear() => _axes.Clear();

    /// <summary>Applies a range to every tracked axis.</summary>
    public void Apply(ChartAxisRange range)
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            _range = range;
            foreach (var axis in _axes)
                axis.SetRange(range);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Reads a source axis and propagates its range when it differs.</summary>
    public bool SyncFrom(IChartXAxisViewport source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_syncing || !source.TryGetRange(out var range) || !IsValid(range))
            return false;
        if (_range == range)
            return false;

        Apply(range);
        return true;
    }

    private static bool IsValid(ChartAxisRange range) =>
        double.IsFinite(range.Minimum)
        && double.IsFinite(range.Maximum)
        && range.Minimum < range.Maximum;

    /// <summary>Clears the stored range so axes return to automatic scaling.</summary>
    public void Reset()
    {
        if (_syncing) return;
        _range = null;
    }
}
