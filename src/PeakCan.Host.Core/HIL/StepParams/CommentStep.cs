namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Documentation step. Not executed. Does NOT affect case pass/fail.
/// </summary>
public record CommentStep(string Text) : StepParameters(TestCaseStepKind.Comment);
