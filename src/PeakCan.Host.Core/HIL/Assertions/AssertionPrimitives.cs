namespace PeakCan.Host.Core.HIL.Assertions;

/// <summary>
/// Assertion primitives for HIL testing. Instance class with injected IAssertionContext.
/// All methods return AssertionResult (never throw on assertion failure).
/// Cancellation (OperationCanceledException) propagates normally.
/// </summary>
public sealed class AssertionPrimitives
{
    private readonly Contracts.IAssertionContext _ctx;

    public AssertionPrimitives(Contracts.IAssertionContext ctx) => _ctx = ctx;

    public async Task<AssertionResult> WaitForSignalAsync(
        string name, double expected, double tolerance, CancellationToken ct)
    {
        // TCS for signal match (completed by callback)
        var matchTcs = new TaskCompletionSource<bool>();

        // TCS for timeout/cancellation (completed when CT fires)
        var timeoutTcs = new TaskCompletionSource<bool>();

        // Register cancellation: cancel timeoutTcs when CT fires
        using var reg = ct.Register(() => timeoutTcs.TrySetCanceled());

        // Subscribe to frames: signal match completes matchTcs
        using var sub = _ctx.SubscribeDecodedFrames(frame =>
        {
            var val = _ctx.GetSignalValue(name);
            if (val is { } v && Math.Abs(v - expected) <= tolerance)
                matchTcs.TrySetResult(true);
        });

        // Await whichever completes first: match or timeout/cancel
        var winner = await Task.WhenAny(matchTcs.Task, timeoutTcs.Task).ConfigureAwait(false);

        if (winner == matchTcs.Task)
        {
            // Signal matched
            return AssertionResult.Pass($"signal {name} = {expected} ±{tolerance}");
        }

        // timeoutTcs.Task completed - this means CT was cancelled
        // Propagate as OperationCanceledException
        throw new OperationCanceledException(ct);
    }

    public AssertionResult AssertSignal(string name, double expected, double tolerance)
    {
        var val = _ctx.GetSignalValue(name);
        if (val is null)
            return AssertionResult.Fail($"signal {name} not found");

        return Math.Abs(val.Value - expected) <= tolerance
            ? AssertionResult.Pass()
            : AssertionResult.Fail($"signal {name} out of tolerance",
                actual: val.Value.ToString(), expected: expected.ToString());
    }

    public AssertionResult AssertRange(string name, double min, double max)
    {
        var val = _ctx.GetSignalValue(name);
        if (val is null)
            return AssertionResult.Fail($"signal {name} not found");

        return val >= min && val <= max
            ? AssertionResult.Pass()
            : AssertionResult.Fail($"signal {name} = {val} outside [{min}, {max}]");
    }
}
