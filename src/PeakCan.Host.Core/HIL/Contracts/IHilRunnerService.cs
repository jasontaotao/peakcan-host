namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Decouples the WPF App layer from the Infrastructure-layer HilRunnerService.
/// App project references Core but not Infrastructure — this interface is the bridge.
/// </summary>
public interface IHilRunnerService
{
    Task<TestSuiteResult> RunAsync(
        Core.HIL.HilRunRequest request,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default);
}
