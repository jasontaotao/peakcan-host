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
            var val = _ctx.GetSignalValue(name, maxAgeMs: 5000);
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
        var val = _ctx.GetSignalValue(name, maxAgeMs: 5000);
        if (val is null)
            return AssertionResult.Fail($"signal {name} not found");

        return Math.Abs(val.Value - expected) <= tolerance
            ? AssertionResult.Pass()
            : AssertionResult.Fail($"signal {name} out of tolerance",
                actual: val.Value.ToString(), expected: expected.ToString());
    }

    public AssertionResult AssertRange(string name, double min, double max)
    {
        var val = _ctx.GetSignalValue(name, maxAgeMs: 5000);
        if (val is null)
            return AssertionResult.Fail($"signal {name} not found");

        return val >= min && val <= max
            ? AssertionResult.Pass()
            : AssertionResult.Fail($"signal {name} = {val} outside [{min}, {max}]");
    }

    public async Task<AssertionResult> WaitForFrameAsync(
        CanId expectedId, byte[]? dataMask, int timeoutMs, CancellationToken ct)
    {
        // Check recent frames first — avoids race condition when frame arrives before subscription
        // (e.g. VirtualEcu responds in microseconds, faster than the subscription can be established)
        var recentFrames = _ctx.GetRecentDecodedFrames();
        foreach (var f in recentFrames)
        {
            if (f.Frame.Id.Raw == expectedId.Raw && MatchesMask(f.Frame.Data, dataMask))
                return AssertionResult.Pass($"frame 0x{expectedId.Raw:X} received (from buffer)");
        }

        var tcs = new TaskCompletionSource<CanFrame>();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeoutMs);

        using var sub = _ctx.SubscribeDecodedFrames(frame =>
        {
            if (frame.Frame.Id.Raw == expectedId.Raw && MatchesMask(frame.Frame.Data, dataMask))
                tcs.TrySetResult(frame.Frame);
        });

        using var registration = linkedCts.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            var matched = await tcs.Task.ConfigureAwait(false);
            return AssertionResult.Pass($"frame 0x{expectedId.Raw:X} received");
        }
        catch (OperationCanceledException)
        {
            return AssertionResult.Fail($"timeout waiting for frame 0x{expectedId.Raw:X} ({timeoutMs}ms)");
        }
    }

    private static bool MatchesMask(ReadOnlyMemory<byte> data, byte[]? mask)
    {
        if (mask is null || mask.Length == 0) return true;
        if (data.Length < mask.Length) return false;
        for (int i = 0; i < mask.Length; i++)
        {
            if ((data.Span[i] & mask[i]) != mask[i]) return false;
        }
        return true;
    }
}
