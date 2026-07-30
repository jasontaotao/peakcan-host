using System.Collections.Concurrent;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Decorator for ICanChannel that injects faults into the receive path (FrameReceived).
/// Wraps an inner channel and applies fault rules before forwarding frames to subscribers.
/// </summary>
public sealed class ReceivePathFaultInjector : ICanChannel
{
    private readonly ICanChannel _inner;
    private readonly object _faultsLock = new();
    private readonly List<FaultRule> _receiveFaults = new();
    private readonly object _subscribersLock = new();
    private Action<CanFrame>? _subscribers;
    private int _subscriberCount;

    public ChannelId Id => _inner.Id;
    public bool IsConnected => _inner.IsConnected;

    public event Action<CanFrame>? FrameReceived
    {
        add
        {
            lock (_subscribersLock) _subscribers += value;
            if (Interlocked.CompareExchange(ref _subscriberCount, 1, 0) == 0)
                _inner.FrameReceived += OnInnerFrameReceived;
            else
                Interlocked.Increment(ref _subscriberCount);
        }
        remove
        {
            lock (_subscribersLock) _subscribers -= value;
            // Guard against underflow: only unsubscribe from inner when count reaches 0
            if (Interlocked.Decrement(ref _subscriberCount) <= 0)
            {
                _inner.FrameReceived -= OnInnerFrameReceived;
                _subscriberCount = 0; // Clamp to 0 to prevent permanent negative
            }
        }
    }

    public event Action<ReadLoopError>? ReadLoopError
    {
        add => _inner.ReadLoopError += value;
        remove => _inner.ReadLoopError -= value;
    }

    public ReceivePathFaultInjector(ICanChannel inner) => _inner = inner;

    /// <summary>Add a receive-path fault rule. Returns a disposable handle for removal.</summary>
    public IDisposable AddReceiveFault(FaultRule fault)
    {
        lock (_faultsLock) _receiveFaults.Add(fault);
        return new FaultHandle(() => { lock (_faultsLock) _receiveFaults.Remove(fault); });
    }

    private readonly ConcurrentDictionary<int, Task> _pendingDelayTasks = new();
    private int _taskIdCounter;

    private void OnInnerFrameReceived(CanFrame frame)
    {
        // Single snapshot for both Delay and non-Delay processing
        List<FaultRule> snapshot;
        lock (_faultsLock) snapshot = _receiveFaults.ToList();

        // Handle Delay faults first
        int maxDelay = snapshot
            .Where(f => f.Type == FaultType.Delay && f.Matches(frame))
            .Select(f => f.DelayMs)
            .DefaultIfEmpty(0)
            .Max();

        if (maxDelay > 0)
        {
            // Async delay: capture subscribers snapshot, delay, then dispatch
            Action<CanFrame>? handler;
            lock (_subscribersLock) handler = _subscribers;
            if (handler is not null)
            {
                var taskId = Interlocked.Increment(ref _taskIdCounter);
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(maxDelay).ConfigureAwait(false);
                        ApplyAndDispatch(frame, handler, snapshot);
                    }
                    finally
                    {
                        _pendingDelayTasks.TryRemove(taskId, out _);
                    }
                });
                _pendingDelayTasks[taskId] = task;
            }
            return;
        }

        ApplyAndDispatch(frame, null, snapshot);
    }

    /// <summary>
    /// Apply non-Delay faults from the pre-taken snapshot and dispatch to subscribers.
    /// </summary>
    private void ApplyAndDispatch(CanFrame frame, Action<CanFrame>? handlerOverride,
        List<FaultRule> snapshot)
    {
        var frames = new List<CanFrame> { frame };
        foreach (var fault in snapshot.Where(f => f.Type != FaultType.Delay))
        {
            if (!fault.Matches(frame)) continue;
            var next = new List<CanFrame>();
            foreach (var f in frames)
                next.AddRange(fault.Apply(f));
            frames = next;
        }

        Action<CanFrame>? handler = handlerOverride;
        if (handler is null)
        {
            lock (_subscribersLock) handler = _subscribers;
        }
        if (handler is null) return;

        foreach (var f in frames)
        {
            foreach (var sub in handler.GetInvocationList())
            {
                try { sub.DynamicInvoke(f); }
                catch { /* isolate per-subscriber exceptions */ }
            }
        }
    }

    /// <summary>
    /// Wait for all pending delay tasks to complete (called from Dispose path).
    /// </summary>
    internal async Task WaitForPendingDelaysAsync(TimeSpan timeout)
    {
        var tasks = _pendingDelayTasks.Values.ToArray();
        if (tasks.Length > 0)
            await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
    }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        => _inner.WriteAsync(frame, ct);

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _inner.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner.DisconnectAsync(ct);

    public async ValueTask DisposeAsync()
    {
        // Wait for pending delay tasks to complete before disposing inner channel
        try { await WaitForPendingDelaysAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { /* force continue with disposal */ }
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
