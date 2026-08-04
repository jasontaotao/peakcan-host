using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Tests.HIL.StepParams;

public class StepParametersExporterTests
{
    private static readonly int[] CorruptIndices = { 0, 2 };

    [Fact]
    public void RoundTrip_SendFrame()
    {
        var p = new SendFrameStep(new CanId(0x123, FrameFormat.Standard), new byte[] { 0x01, 0x02 }, Fd: true, Extended: false);
        var dict = StepParametersExporter.FromParameters(p);
        StepParametersFactory.Create(TestCaseStepKind.SendFrame, dict).Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_ExpectFrame_With_DataMask()
    {
        var p = new ExpectFrameStep(new CanId(0x456, FrameFormat.Extended), new byte[] { 0x00, 0x7E }, 5000);
        StepParametersFactory.Create(TestCaseStepKind.WaitForFrame, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_ExpectFrame_Null_DataMask()
    {
        var p = new ExpectFrameStep(new CanId(0x456, FrameFormat.Standard), null, 5000);
        StepParametersFactory.Create(TestCaseStepKind.WaitForFrame, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_WaitForSignal()
    {
        var p = new WaitForSignalStep("BMS_Status.SOC", 80.5, 1.0, 3000);
        StepParametersFactory.Create(TestCaseStepKind.WaitForSignal, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertSignal()
    {
        var p = new AssertSignalStep("M1.Speed", 100, 0.5);
        StepParametersFactory.Create(TestCaseStepKind.AssertSignal, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertRange()
    {
        var p = new AssertRangeStep("M1.Temp", 10, 90);
        StepParametersFactory.Create(TestCaseStepKind.AssertRange, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertResponseTime()
    {
        var p = new AssertResponseTimeStep(new CanId(0x7E0, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), 100);
        StepParametersFactory.Create(TestCaseStepKind.AssertResponseTime, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertDtc_With_And_Without_Code()
    {
        StepParametersFactory.Create(TestCaseStepKind.AssertDtc, StepParametersExporter.FromParameters(new AssertDtcStep(0x22, true)))
            .Should().BeEquivalentTo(new AssertDtcStep(0x22, true));
        StepParametersFactory.Create(TestCaseStepKind.AssertDtc, StepParametersExporter.FromParameters(new AssertDtcStep(null, false)))
            .Should().BeEquivalentTo(new AssertDtcStep(null, false));
    }

    [Fact]
    public void RoundTrip_AssertNrc()
    {
        var p = new AssertNrcStep(0x22, 0x31);
        StepParametersFactory.Create(TestCaseStepKind.AssertNrc, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_Delay()
    {
        var p = new DelayStep(250);
        StepParametersFactory.Create(TestCaseStepKind.Delay, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_Comment()
    {
        var p = new CommentStep("check engine on");
        StepParametersFactory.Create(TestCaseStepKind.Comment, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_InjectFault_All_Fields()
    {
        var p = new InjectFaultStep(
            new CanId(0x123, FrameFormat.Standard), FaultType.Corrupt, 0.5, 10,
            CorruptIndices, 0xFF, "fault1", FaultDirection.Both);
        StepParametersFactory.Create(TestCaseStepKind.InjectFault, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_InjectFault_Defaults_Optional()
    {
        var p = new InjectFaultStep(new CanId(0x123, FrameFormat.Standard), FaultType.Drop, 1.0, 0, null, 0xFF, null);
        StepParametersFactory.Create(TestCaseStepKind.InjectFault, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_ClearFault_With_And_Without_Id()
    {
        StepParametersFactory.Create(TestCaseStepKind.ClearFault, StepParametersExporter.FromParameters(new ClearFaultStep("f1")))
            .Should().BeEquivalentTo(new ClearFaultStep("f1"));
        StepParametersFactory.Create(TestCaseStepKind.ClearFault, StepParametersExporter.FromParameters(new ClearFaultStep(null)))
            .Should().BeEquivalentTo(new ClearFaultStep(null));
    }
}
