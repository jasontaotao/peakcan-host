using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.HIL.Fakes;

/// <summary>
/// Test fixture for testing ITestFixture lifecycle.
/// </summary>
internal sealed class FakeFixture : ITestFixture
{
    public int SetupCallCount { get; private set; }
    public int TeardownCallCount { get; private set; }

    public Task SetupAsync(IAssertionContext ctx, CancellationToken ct)
    {
        SetupCallCount++;
        return Task.CompletedTask;
    }

    public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct)
    {
        TeardownCallCount++;
        return Task.CompletedTask;
    }
}
