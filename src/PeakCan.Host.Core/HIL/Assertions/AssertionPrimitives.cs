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

        // Subscribe to frames: signal match completes matchTcs
        using var sub = _ctx.SubscribeDecodedFrames(frame =>
        {
            var val = _ctx.GetSignalValue(name);
            if (val is { } v && Math.Abs(v - expected) <= tolerance)
                matchTcs.TrySetResult(true);
        });

        // Create a task that completes when CT is cancelled
        var cancelTask = Task.Delay(Timeout.Infinite, ct);

        // Await whichever completes first: match or cancel
        var winner = await Task.WhenAny(matchTcs.Task, cancelTask).ConfigureAwait(false);

        if (winner == matchTcs.Task)
        {
            // Signal matched
            return AssertionResult.Pass($"signal {name} = {expected} ±{tolerance}");
        }

        // cancelTask completed - CT was cancelled. Await to propagate the OperationCanceledException.
        await cancelTask.ConfigureAwait(false);
        throw new OperationCanceledException(ct); // Fallback
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
