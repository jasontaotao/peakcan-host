using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.CanChannels;

/// <summary>
/// Pure in-process frame router — no trace file, no hardware.
/// Implements <see cref="ICanChannel"/> with a bounded <see cref="global::System.Threading.Channels.Channel{CanFrame}"/> as the frame bus.
/// </summary>
public sealed class VirtualChannel : ICanChannel
{
    private readonly global::System.Threading.Channels.Channel<CanFrame> _frameChannel;
    private readonly object _subscribersLock = new();
    private Action<CanFrame>? _frameReceived;
    private int _isConnected; // 0=disconnected, 1=connected (CAS)
    private readonly CancellationTokenSource _consumerCts = new();
    private Task _consumerTask = Task.CompletedTask;

    public ChannelId Id => ChannelId.None;
    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;

    public event Action<CanFrame>? FrameReceived
    {
        add { lock (_subscribersLock) _frameReceived += value; }
        remove { lock (_subscribersLock) _frameReceived -= value; }
    }

    public event Action<ReadLoopError>? ReadLoopError
    {
        add { /* virtual channel has no hardware read loop */ }
        remove { /* virtual channel has no hardware read loop */ }
    }

    public VirtualChannel(int capacity = 1000)
    {
        _frameChannel = global::System.Threading.Channels.Channel.CreateBounded<CanFrame>(
            new global::System.Threading.Channels.BoundedChannelOptions(capacity)
            {
                FullMode = global::System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _isConnected, 1, 0) == 1)
            return Task.FromResult(Result<Unit>.Ok(default)); // idempotent: already connected

        if (_disposed == 1)
            return Task.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Channel disposed"));

        // Use internal CTS — do not bind to caller's CancellationToken
        _consumerTask = Task.Run(() => ConsumerLoop(_consumerCts.Token));
        return Task.FromResult(Result<Unit>.Ok(default));
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _isConnected, 0);
        _consumerCts.Cancel();
        _frameChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        // DropOldest: TryWrite only returns false when channel is completed
        if (!_frameChannel.Writer.TryWrite(frame))
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Virtual channel closed"));
        return ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    private async Task ConsumerLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Snapshot subscribers under lock, then invoke outside lock
                Action<CanFrame>? handler;
                lock (_subscribersLock) handler = _frameReceived;

                // Per-subscriber exception isolation (matches ChannelRouter pattern).
                // In the HIL scenario, VirtualEcu and HILAssertionContext subscribe
                // directly — a throw from one must not kill the bus for others.
                if (handler is not null)
                {
                    foreach (var subscriber in handler.GetInvocationList())
                    {
                        try { subscriber.DynamicInvoke(frame); }
                        catch (Exception ex)
                        {
                            // Log but do not re-throw — other subscribers must still receive frames
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (global::System.Threading.Channels.ChannelClosedException) { /* normal shutdown */ }
    }

    private int _disposed; // 0=active, 1=disposed (CAS for idempotency)

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // idempotent
        _consumerCts.Cancel();
        _frameChannel.Writer.TryComplete();
        try { await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (TimeoutException) { /* consumer thread stuck — force cancel */ }
        _consumerCts.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // idempotent
        _consumerCts.Cancel();
        _frameChannel.Writer.TryComplete();
        _consumerCts.Dispose();
    }
}
