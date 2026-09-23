using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Infrastructure.Xcp;

/// <summary>
/// IXcpTransport 的 CAN 适配层：复用 <see cref="ICanChannel"/>
/// （ConnectAsync / WriteAsync / FrameReceived，spec §3 Infrastructure）。
/// <para>
/// S2-T1-review 修复：响应帧（PID 0xFF/0xFE）与 DAQ DTO 分流入队——
/// 响应走独立无界队列（命令应答体量小，每命令至多一帧，永不丢弃），
/// DTO 保持有界 DropOldest（最新值优先、读线程永不阻塞）。
/// 两队列由同一条分发循环串行派发且响应优先，订阅者回调仍不并发；
/// DTO 丢弃计入 <see cref="FramesDropped"/>，供命令层超时归因查证
/// （丢帧数增长 = 采集洪泛挤占，而非应答帧丢失）。
/// </para>
/// </summary>
public sealed class XcpCanTransport : IXcpTransport
{
    /// <summary>默认入队容量（100 Hz × 15 ODT 量级下远超够用）。</summary>
    public const int DefaultQueueCapacity = 1024;

    private readonly ICanChannel _channel;
    private readonly ConcurrentQueue<CanFrame> _responses = new();
    private readonly ConcurrentQueue<CanFrame> _dtos = new();
    private readonly int _dtoCapacity;
    private readonly SemaphoreSlim _pending = new(0);
    private readonly Task _dispatchLoop;
    private long _framesDropped;
    private volatile bool _completed;
    private bool _disposed;

    /// <inheritdoc />
    public event Action<CanFrame>? FrameReceived;

    /// <summary>
    /// 因 DTO 队列满而被 DropOldest 丢弃的帧数（响应帧不经过有界队列，
    /// 不会被丢弃，因此不计入）。命令层超时归因：计数增长说明采集流
    /// 洪泛挤占，命令应答仍完整。
    /// </summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    public XcpCanTransport(ICanChannel channel, int queueCapacity = DefaultQueueCapacity)
    {
        if (queueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "Queue capacity must be positive.");

        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _dtoCapacity = queueCapacity;
        _dispatchLoop = Task.Run(DispatchLoopAsync);
        _channel.FrameReceived += EnqueueFromReadThread;
    }

    /// <summary>
    /// 读线程回调：只入队，绝不等待分发端（spec §3 写死）。
    /// 单写线程假设与原 BoundedChannel SingleWriter 语义一致；
    /// ConcurrentQueue 保证即使假设被破坏也不丢崩溃，仅丢帧计数可能偏差。
    /// </summary>
    private void EnqueueFromReadThread(CanFrame frame)
    {
        // XCP 帧 PID 恒在首字节；空 Data 防御性归入 DTO 流。
        var pid = frame.Data.Length > 0 ? frame.Data.Span[0] : (byte)0;
        if (pid == XcpPid.PositiveResponse || pid == XcpPid.Error)
        {
            // 响应无界：命令应答必须送达，由命令层负责消费/超时。
            _responses.Enqueue(frame);
        }
        else
        {
            // DTO 有界 DropOldest：单写线程下丢帧计数精确。
            while (_dtos.Count >= _dtoCapacity)
            {
                _dtos.TryDequeue(out _);
                Interlocked.Increment(ref _framesDropped);
            }

            _dtos.Enqueue(frame);
        }

        _pending.Release();
    }

    /// <summary>
    /// 分发循环：响应优先、两队列串行派发；单个订阅者异常不得终止循环。
    /// </summary>
    private async Task DispatchLoopAsync()
    {
        while (true)
        {
            await _pending.WaitAsync().ConfigureAwait(false);
            DispatchQueued();

            // DisposeAsync 置位 _completed 并补发一次信号；清空后退出。
            if (_completed && _responses.IsEmpty && _dtos.IsEmpty)
                return;
        }
    }

    /// <summary>排空两队列并派发（响应先于 DTO，同流内保持到达顺序）。</summary>
    private void DispatchQueued()
    {
        while (_responses.TryDequeue(out var response))
            Dispatch(response);

        while (_dtos.TryDequeue(out var dto))
            Dispatch(dto);
    }

    /// <summary>派发单帧到全部订阅者；异常隔离在单个订阅者内。</summary>
    private void Dispatch(CanFrame frame)
    {
        var handler = FrameReceived;
        if (handler is null) return;

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
        _completed = true;
        _pending.Release();
        await _dispatchLoop.ConfigureAwait(false);
        _pending.Dispose();
    }
}
