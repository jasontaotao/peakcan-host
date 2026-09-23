using PeakCan.HIL.Core;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Xcp.Abstractions;

namespace PeakCan.Host.Infrastructure.Xcp;

/// <summary>
/// IXcpTransport 的 CAN 适配层：复用 <see cref="ICanChannel"/>
/// （ConnectAsync / WriteAsync / FrameReceived，spec §3 Infrastructure）。
/// 读线程回调只入队即返回（队列有界、丢旧不阻塞），分发在独立循环线程上
/// 进行——对齐 VirtualEcu→IsoTpLayer 的“读线程不阻塞”先例。
/// </summary>
public sealed class XcpCanTransport : IXcpTransport
{
    /// <summary>默认入队容量（100 Hz × 15 ODT 量级下远超够用）。</summary>
    public const int DefaultQueueCapacity = 1024;

    private readonly ICanChannel _channel;
    private readonly System.Threading.Channels.Channel<CanFrame> _queue;
    private readonly Task _dispatchLoop;
    private bool _disposed;

    /// <inheritdoc />
    public event Action<CanFrame>? FrameReceived;

    public XcpCanTransport(ICanChannel channel, int queueCapacity = DefaultQueueCapacity)
    {
        if (queueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "Queue capacity must be positive.");

        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _queue = System.Threading.Channels.Channel.CreateBounded<CanFrame>(new System.Threading.Channels.BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // 丢旧保新：采集流是最新值优先，且必须保证读线程永不阻塞。
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
        });

        _channel.FrameReceived += EnqueueFromReadThread;
        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    /// <summary>读线程回调：只入队，绝不等待分发端（spec §3 写死）。</summary>
    private void EnqueueFromReadThread(CanFrame frame) => _queue.Writer.TryWrite(frame);

    /// <summary>分发循环：逐帧回调订阅者；单个订阅者异常不得终止循环。</summary>
    private async Task DispatchLoopAsync()
    {
        await foreach (var frame in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var handler = FrameReceived;
            if (handler is null) continue;

            foreach (Action<CanFrame> sub in handler.GetInvocationList())
            {
                try
                {
                    sub(frame);
                }
                catch
                {
                    // 订阅者是尽力而为的消费者：异常隔离在单个订阅者内，
                    // 分发循环与后续帧不受影响（与 ChannelRouter sink 隔离同型）。
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        => _channel.WriteAsync(frame, ct);

    /// <summary>
    /// Stop dispatching and detach from the channel. The underlying channel
    /// is owned by the caller and is NOT disposed here.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.FrameReceived -= EnqueueFromReadThread;
        _queue.Writer.TryComplete();
        await _dispatchLoop.ConfigureAwait(false);
    }
}

