namespace PeakCan.Host.App.ViewModels;

public sealed partial class MultiFrameSendViewModel
{
    // Flow E: Lifecycle (v1.x.x + earlier).
    // Methods moved verbatim from MultiFrameSendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Dispose -> _progressPollTimer + _runCts (intra-flow state, main)
    //   - Dispose -> _dbcService.DbcLoaded -= OnDbcLoaded (Flow D handler)

    public void Dispose()
    {
        _progressPollTimer.Stop();
        _runCts?.Cancel();
        _runCts?.Dispose();
        if (_dbcService is not null)
            _dbcService.DbcLoaded -= OnDbcLoaded;
        GC.SuppressFinalize(this);
    }
}