using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// Regression for the F1 (Rev10 follow-up) idempotent-dispose fix: the DI
/// container and the run engine both dispose the context, so a second
/// <see cref="HILAssertionContext.Dispose"/> must not throw
/// ObjectDisposedException from <c>_consumerCts.Cancel()</c> during
/// --ecu/--matrix/--trace teardown.
/// </summary>
public class HILAssertionContextDisposeTests
{
    [Fact]
    public void Dispose_IsIdempotent()
    {
        var ctx = new HILAssertionContext(new FakeCanChannel(), new FakeDbcLookup());

        ctx.Dispose();
        ctx.Dispose(); // second call must be a no-op, not a throw
    }
}
