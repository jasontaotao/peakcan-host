using Xunit;

namespace PeakCan.Host.Core.Tests.Architecture;

public class HILLayeringTests
{
    [Fact]
    public void HIL_assembly_does_not_reference_Infrastructure_or_App()
    {
        var assembly = typeof(PeakCan.Host.Core.HIL.TestCase).Assembly;
        var refs = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("PeakCan.Host.Infrastructure", refs);
        Assert.DoesNotContain("PeakCan.Host.App", refs);
    }
}
