namespace PeakCan.Host.Core.HIL.Assertions;

/// <summary>
/// Result of a single assertion evaluation.
/// </summary>
public sealed record AssertionResult(
    bool Passed,
    string? Message,
    string? ActualValue,
    string? ExpectedValue)
{
    public static AssertionResult Pass(string? msg = null) => new(true, msg, null, null);

    public static AssertionResult Fail(string msg, string? actual = null, string? expected = null)
        => new(false, msg, actual, expected);
}
