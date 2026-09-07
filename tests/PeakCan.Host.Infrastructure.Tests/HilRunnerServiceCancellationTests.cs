using PeakCan.Host.Core;
using PeakCan.HIL.Core;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

public class HilRunnerServiceCancellationTests
{
    [Fact]
    public async Task Disconnect_InFinally_UsesCancellationTokenNone()
    {
        var token = new CancellationToken(true);
        var channel = new RecordingDisconnectChannel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.DisconnectAsync(token));
        Assert.Equal(token, channel.LastDisconnectToken);
    }

    private sealed class RecordingDisconnectChannel : ICanChannel
    {
        public ChannelId Id => new(1);
        public bool IsConnected { get; private set; }
        public CancellationToken LastDisconnectToken { get; private set; }
        public event Action<CanFrame>? FrameReceived { add { } remove { } }
        public event Action<ReadLoopError>? ReadLoopError { add { } remove { } }

        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.FromResult(Result<Unit>.Ok(default));
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            LastDisconnectToken = ct;
            ct.ThrowIfCancellationRequested();
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<Unit>.Ok(default));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
