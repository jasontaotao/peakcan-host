using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Decorator for ICanChannel that injects faults into the send path (WriteAsync).
/// Wraps an inner channel and applies active fault rules before forwarding frames.
/// </summary>
public sealed class FaultInjector : ICanChannel
{
    private readonly ICanChannel _inner;
    private readonly object _faultsLock = new();
    private readonly List<FaultRule> _activeFaults = new();

    public ChannelId Id => _inner.Id;
    public bool IsConnected => _inner.IsConnected;

    public event Action<CanFrame>? FrameReceived
    {
        add => _inner.FrameReceived += value;
        remove => _inner.FrameReceived -= value;
    }

    public event Action<ReadLoopError>? ReadLoopError
    {
        add => _inner.ReadLoopError += value;
        remove => _inner.ReadLoopError -= value;
    }

    public FaultInjector(ICanChannel inner) => _inner = inner;

    /// <summary>Add a fault rule. Returns a disposable handle for removal.</summary>
    public FaultHandle AddFault(FaultRule fault)
    {
        lock (_faultsLock) _activeFaults.Add(fault);
        return new FaultHandle(() => RemoveFault(fault));
    }

    private void RemoveFault(FaultRule fault)
    {
        lock (_faultsLock) _activeFaults.Remove(fault);
    }

    public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        List<FaultRule>? snapshot;
        lock (_faultsLock) snapshot = _activeFaults.Count > 0 ? _activeFaults.ToList() : null;

        if (snapshot is null || snapshot.Count == 0)
            return await _inner.WriteAsync(frame, ct).ConfigureAwait(false);

        // Check Delay faults — take maximum delay
        int maxDelay = snapshot
            .Where(f => f.Type == FaultType.Delay && f.Matches(frame))
            .Select(f => f.DelayMs)
            .DefaultIfEmpty(0)
            .Max();

        if (maxDelay > 0)
            await Task.Delay(maxDelay, ct).ConfigureAwait(false);

        // Apply non-Delay faults
        var frames = new List<CanFrame> { frame };
        foreach (var fault in snapshot.Where(f => f.Type != FaultType.Delay))
        {
            if (!fault.Matches(frame)) continue;
            var next = new List<CanFrame>();
            foreach (var f in frames)
                next.AddRange(fault.Apply(f));
            frames = next;
        }

        // If all frames dropped, return success
        if (frames.Count == 0)
            return Result<Unit>.Ok(default);

        foreach (var f in frames)
        {
            var result = await _inner.WriteAsync(f, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
        }

        return Result<Unit>.Ok(default);
    }

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _inner.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner.DisconnectAsync(ct);

    // ICanChannel inherits IAsyncDisposable, not IDisposable. Only implement DisposeAsync.
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

public sealed record FaultHandle(Action Remove) : IDisposable
{
    public void Dispose() => Remove();
}
