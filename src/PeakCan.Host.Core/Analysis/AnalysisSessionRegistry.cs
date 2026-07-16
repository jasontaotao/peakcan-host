namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: version-aware analysis session store. Independent
/// of TraceViewerViewModel.Reset per hard-boundary #8. CreateOrUpdate
/// increments Version monotonically; consumers compare Version to detect
/// staleness (e.g. when a new anchor snapshot is captured, the previous
/// session is "stale" but still queryable until Clear).</summary>
public class AnalysisSessionRegistry
{
    private AnalysisSession? _current;
    private int _versionCounter;

    public AnalysisSession? CurrentSession => _current;

    public AnalysisSession CreateOrUpdate(AnalysisSession newSession)
    {
        ArgumentNullException.ThrowIfNull(newSession);
        _versionCounter++;
        var stamped = newSession with { Version = _versionCounter };
        _current = stamped;
        return stamped;
    }

    public void Clear() => _current = null;
}
