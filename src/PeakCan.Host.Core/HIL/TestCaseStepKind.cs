namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Discriminator for test step types. Each value maps to one StepParameters subclass.
/// </summary>
public enum TestCaseStepKind
{
    SendFrame,
    SendSequence,        // Reserved for Sprint 2
    WaitForFrame,
    WaitForSignal,
    AssertSignal,
    AssertRange,
    AssertDtc,
    AssertNrc,
    AssertResponseTime,
    Delay,
    Comment,
    InjectFault,    // Phase 3 Sprint 5
    ClearFault,     // Phase 3 Sprint 5
}
