using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Views;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

public sealed class HilViewFailureCopyTests
{
    [Fact]
    public void BuildFailureDetail_IncludesAllFields()
    {
        var caseNode = new TestCaseNode { Name = "Case" };
        var stepNode = new StepNode
        {
            Name = "Step",
            Status = "Failed",
            Message = "mismatch",
            ActualValue = "0",
            ExpectedValue = "1",
            Channel = "bus-a",
        };
        stepNode.Frames.Add(new FrameNode { CanId = "0x100", DataHex = "01 02" });

        var detail = HilViewFailureCopy.BuildFailureDetail("Suite", caseNode, stepNode);

        Assert.Contains("Suite: Suite", detail);
        Assert.Contains("Case: Case", detail);
        Assert.Contains("Step: Step", detail);
        Assert.Contains("Frames: 0x100 01 02", detail);
    }
}
