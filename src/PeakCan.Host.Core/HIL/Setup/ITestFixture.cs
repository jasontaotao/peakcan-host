using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL.Setup;

/// <summary>
/// Test fixture. Single interface for both Suite-level and Case-level fixtures,
/// distinguished by DI registration key.
/// Setup failure: Teardown still executes, fixture marked as failed.
/// Teardown failure: logged, does not mask original failure.
/// </summary>
public interface ITestFixture
{
    Task SetupAsync(IAssertionContext ctx, CancellationToken ct);
    Task TeardownAsync(IAssertionContext ctx, CancellationToken ct);
}
