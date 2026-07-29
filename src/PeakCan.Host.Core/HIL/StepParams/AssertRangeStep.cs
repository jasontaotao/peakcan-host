namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for asserting a signal is within [min, max] range.
/// </summary>
public record AssertRangeStep(string SignalName, double Min, double Max)
    : StepParameters(TestCaseStepKind.AssertRange);
