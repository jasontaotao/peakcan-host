using FluentAssertions;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.Tests.ViewModels.TestSuiteBuilder;

public class StepFieldDescriptorsTests
{
    [Fact]
    public void Every_Kind_Has_Descriptors_And_Defaults_Build_A_Valid_Step()
    {
        foreach (var kind in StepFieldDescriptors.AllKinds)
        {
            StepFieldDescriptors.For(kind).Should().NotBeEmpty($"{kind} needs at least one field");
            var built = StepParametersFactory.Create(kind, StepFieldDescriptors.DefaultsFor(kind));
            built.Kind.Should().Be(kind, $"{kind} defaults must build a valid step");
        }
    }
}
