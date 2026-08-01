using FluentAssertions;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.Tests.ViewModels.TestSuiteBuilder;

public class EditableModelTests
{
    private static readonly string[] SmokeTags = { "smoke" };

    [Fact]
    public void New_SendFrame_Has_Defaults_And_Builds_Valid_Step()
    {
        var step = EditableTestCaseStep.New(TestCaseStepKind.SendFrame);
        step.Kind.Should().Be(TestCaseStepKind.SendFrame);
        var built = step.ToStep();
        built.Kind.Should().Be(TestCaseStepKind.SendFrame);
        built.Parameters.Should().BeOfType<SendFrameStep>();
    }

    [Fact]
    public void FromStep_Then_ToStep_RoundTrips()
    {
        var original = TestCaseStep.Create(
            new AssertSignalStep("M1.Speed", 100, 0.5), "check speed");
        var editable = EditableTestCaseStep.FromStep(original);
        editable.ToStep().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Editing_Params_Reflects_In_ToStep()
    {
        var step = EditableTestCaseStep.New(TestCaseStepKind.AssertSignal);
        step.Params["SignalName"] = "M1.Speed";
        step.Params["Expected"] = 88.0;
        step.ToStep().Parameters.Should().BeEquivalentTo(new AssertSignalStep("M1.Speed", 88.0, 0));
    }

    [Fact]
    public void TestCase_RoundTrip()
    {
        var c = new TestCase(
            Id: "case_1", Name: "TP", Description: "d", PreConditions: null,
            Steps: new List<TestCaseStep> { TestCaseStep.Create(new DelayStep(100)) },
            PostConditions: null, Tags: SmokeTags, TimeoutMs: 5000,
            CaseFixtureKeys: null);
        var editable = EditableTestCase.FromCase(c);
        editable.ToCase().Should().BeEquivalentTo(c);
    }
}
