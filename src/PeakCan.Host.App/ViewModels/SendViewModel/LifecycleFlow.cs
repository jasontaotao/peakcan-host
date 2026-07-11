namespace PeakCan.Host.App.ViewModels;

public sealed partial class SendViewModel
{
    // Flow D: Lifecycle (v1.2.11 PATCH review fix).
    // Methods moved verbatim from SendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Dispose -> _pollTimer (intra-flow state, stays in main file)

    /// <summary>
    /// v1.2.11 PATCH review fix: stop and detach the poll timer so the VM
    /// can be GC'd after the shell navigates away. Production callers
    /// should dispose the VM when the Send tab is closed; tests ignore
    /// (timer keeps running but the xunit fixture ends before it matters).
    /// </summary>
    public void Dispose()
    {
        _pollTimer.Stop();
        GC.SuppressFinalize(this);
    }
}