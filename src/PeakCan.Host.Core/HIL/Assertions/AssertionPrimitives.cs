namespace PeakCan.Host.Core.HIL.Assertions;

/// <summary>
/// Assertion primitives for HIL testing. Instance class with injected IAssertionContext.
/// All methods return AssertionResult (never throw on assertion failure).
/// Cancellation (OperationCanceledException) propagates only when CT is externally cancelled before any match.
/// </summary>
public sealed class AssertionPrimitives
{
    private readonly Contracts.IAssertionContext _ctx;

    public AssertionPrimitives(Contracts.IAssertionContext ctx) => _ctx = ctx;

    public async Task<AssertionResult> WaitForSignalAsync(
        string name, double expected, double tolerance, CancellationToken ct)
    {
        var matchTcs = new TaskCompletionSource<bool>();

        using var sub = _ctx.SubscribeDecodedFrames(frame =>
        {
            var val = _ctx.GetSignalValue(name);
            if (val is { } v && Math.Abs(v - expected) <= tolerance)
                matchTcs.TrySetResult(true);
        });

        // Task.Delay with CT: throws OperationCanceledException when CT fires
        var cancelTask = Task.Delay(Timeout.Infinite, ct);

        var winner = await Task.WhenAny(matchTcs.Task, cancelTask).ConfigureAwait(false);

        if (winner == matchTcs.Task)
            return AssertionResult.Pass($"signal {name} = {expected} ±{tolerance}");

        // cancelTask won (CT cancelled). Await to trigger exception propagation.
        try
        {
            await cancelTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout/cancellation during wait → return failure result
            return AssertionResult.Fail($"timeout waiting for {name} = {expected} ±{tolerance}",
                actual: _ctx.GetSignalValue(name)?.ToString());
        }

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
