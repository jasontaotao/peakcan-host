using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// Minimal standalone fake ICanChannel for unit/integration tests.
/// WriteAsync routes frames back to FrameReceived subscribers (loopback).
/// No real hardware, no async consumer thread — synchronous and deterministic.
/// </summary>
public sealed class FakeCanChannel : ICanChannel
{
    private int _isConnected; // 0=disconnected, 1=connected
    private int _disposed;

    public ChannelId Id { get; }

    public FakeCanChannel(ushort handle = 0x51) => Id = new ChannelId(handle);
    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;

    public event Action<CanFrame>? FrameReceived;

#pragma warning disable CS0067 // Fake channel has no read loop; event required by interface contract.
    public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067

    /// <summary>All frames written via WriteAsync (in order).</summary>
    public IReadOnlyList<CanFrame> WrittenFrames => _writtenFrames;
    private readonly List<CanFrame> _writtenFrames = new();

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _isConnected, 1);
        return Task.FromResult(Result<Unit>.Ok(default));
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _isConnected, 0);
        return Task.CompletedTask;
    }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _isConnected) == 0)
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Not connected."));
        if (Volatile.Read(ref _disposed) == 1)
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Channel disposed."));

        _writtenFrames.Add(frame);
        // Loopback: raise FrameReceived synchronously so subscribers react immediately.
        FrameReceived?.Invoke(frame);
        return ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    /// <summary>Simulate frame reception (raises FrameReceived). Deterministic for tests.</summary>
    public void SimulateFrame(CanFrame frame) => FrameReceived?.Invoke(frame);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        Interlocked.Exchange(ref _isConnected, 0);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Interlocked.Exchange(ref _isConnected, 0);
    }
}
