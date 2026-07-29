using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Headless fixture resolver. Returns NoOpTestFixture for any key.
/// </summary>
internal sealed class HeadlessFixtureResolver : IFixtureResolver
{
    private static readonly ITestFixture NoOp = new NoOpTestFixture();
    public ITestFixture Resolve(string key) => NoOp;
}

/// <summary>
/// No-op fixture for headless execution.
/// </summary>
internal sealed class NoOpTestFixture : ITestFixture
{
    public Task SetupAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
    public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
}
