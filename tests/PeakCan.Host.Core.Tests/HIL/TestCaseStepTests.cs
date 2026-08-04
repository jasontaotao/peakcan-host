using PeakCan.HIL.Core.HIL;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL;

public class TestCaseStepTests
{
    [Fact]
    public void Create_KindMatchesParametersKind()
    {
        var parameters = new WaitForSignalStep("Test", 100.0, 10.0, 5000);
        var step = TestCaseStep.Create(parameters);

        Assert.Equal(TestCaseStepKind.WaitForSignal, step.Kind);
    }

    [Fact]
    public void Create_PreservesLabel()
    {
        var parameters = new CommentStep("doc");
        var step = TestCaseStep.Create(parameters, "my label");

        Assert.Equal("my label", step.Label);
        Assert.Equal(TestCaseStepKind.Comment, step.Kind);
    }

    [Fact]
    public void Create_NullLabel_OK()
    {
        var parameters = new DelayStep(100);
        var step = TestCaseStep.Create(parameters);

        Assert.Null(step.Label);
    }

    [Fact]
    public void Create_ForEachStepKind_MatchesParameters()
    {
        StepParameters[] parameters =
        [
            new SendFrameStep(new CanId(0x7DF, FrameFormat.Standard), new byte[8], false, false),
            new WaitForSignalStep("S", 1.0, 0.1, 1000),
            new AssertSignalStep("S", 1.0, 0.1),
            new AssertRangeStep("S", 0.0, 100.0),
            new ExpectFrameStep(new CanId(0x7DF, FrameFormat.Standard), null, 1000),
            new AssertResponseTimeStep(new CanId(0x7E0, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), 100),
            new AssertDtcStep(0x1234, true),
            new AssertNrcStep(0x22, 0x31),
            new DelayStep(100),
            new CommentStep("text"),
        ];

        foreach (var p in parameters)
        {
            var step = TestCaseStep.Create(p);
            Assert.Equal(p.Kind, step.Kind);
            Assert.Equal(p.Kind, step.Parameters.Kind);
        }
    }
}
