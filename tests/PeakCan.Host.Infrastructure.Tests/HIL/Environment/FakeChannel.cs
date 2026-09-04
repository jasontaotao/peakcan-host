using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public sealed class FakeChannel : ICanChannel
{
    public Action<CanFrame>? OnWrite { get; set; }
    private event Action<CanFrame>? _frameReceived;
    public ChannelId Id => default;
    public bool IsConnected => true;

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => Task.FromResult(Result<Unit>.Ok(default(Unit)));

    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        OnWrite?.Invoke(frame);
        return ValueTask.FromResult(Result<Unit>.Ok(default(Unit)));
    }

    public event Action<CanFrame>? FrameReceived
    {
        add => _frameReceived += value;
        remove => _frameReceived -= value;
    }

    public void RaiseFrameReceived(CanFrame frame) => _frameReceived?.Invoke(frame);

    public event Action<ReadLoopError>? ReadLoopError { add { } remove { } }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}