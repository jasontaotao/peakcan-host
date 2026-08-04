using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Virtual CAN channel that replays ASC trace files.
/// Raises <see cref="ICanChannel.FrameReceived"/> directly (no ChannelRouter).
/// WriteAsync is a no-op (no physical bus in Sprint 2).
/// </summary>
public sealed class TraceDrivenChannel : ICanChannel
{
    private readonly ChannelId _id;
    private readonly ILogger<TraceDrivenChannel>? _logger;
    private readonly int _maxFramesPerTick;
    private readonly int _maxTraceFrames;
    private readonly List<ReplayFrame> _frames = new();
    private readonly object _framesLock = new();
    private int _nextFrameIndex;
    private System.Threading.Timer? _timer;
    private long _state; // 0=Idle, 1=CallbackInProgress, 2=Disposing
    private DateTime _playStartWallClock;
    private double _playStartTimestamp = -1;
    private double _speed = 1.0;
    private readonly List<CanFrame> _emitBuffer = new(capacity: 128);
    private int _maxEmittedPerTick; // Diagnostic: tracks peak single-tick emit count

    public ChannelId Id => _id;
    public bool IsConnected { get; private set; }

    /// <summary>Diagnostic: peak number of frames emitted in a single OnTick call. For testing MaxFramesPerTick.</summary>
    internal int MaxEmittedPerTick => _maxEmittedPerTick;
    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;

    public TraceDrivenChannel(
        ChannelId id,
        ILogger<TraceDrivenChannel>? logger = null,
        int maxFramesPerTick = 100,
        int maxTraceFrames = 2_000_000)
    {
        _id = id;
        _logger = logger;
        _maxFramesPerTick = maxFramesPerTick;
        _maxTraceFrames = maxTraceFrames;
    }

    public void LoadAscii(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_state == 2, this);

        if (IsConnected)
            throw new InvalidOperationException("Cannot load trace while playing. Disconnect first.");

        if (!File.Exists(path))
            throw new FileNotFoundException("ASC trace file not found.", path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        IReadOnlyList<ReplayFrame> frames;
        try
        {
            frames = AscParser.ParseAsync(stream, ct).GetAwaiter().GetResult();
        }
        catch (ReplayFormatException)
        {
            // Empty or malformed trace: treat as zero frames
            frames = Array.Empty<ReplayFrame>();
        }

        if (frames.Count > _maxTraceFrames)
            throw new InvalidOperationException(
                $"Trace file has {frames.Count} frames, exceeds MaxTraceFrames={_maxTraceFrames}.");

        lock (_framesLock)
        {
            _frames.Clear();
            _frames.AddRange(frames);
            _nextFrameIndex = 0;
            _playStartTimestamp = frames.Count > 0 ? frames[0].Timestamp : -1;
        }

        _logger?.LogInformation("Loaded ASC trace: {FrameCount} frames, first timestamp={Timestamp}s",
            frames.Count, _playStartTimestamp);
    }

    public void LoadBlf(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_state == 2, this);

        if (IsConnected)
            throw new InvalidOperationException("Cannot load trace while playing. Disconnect first.");

        if (!File.Exists(path))
            throw new FileNotFoundException("BLF trace file not found.", path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frames = BlfParser.ParseAsync(stream, ReplayOptions.Default, logger: null, ct)
            .GetAwaiter().GetResult();

        if (frames.Count > _maxTraceFrames)
            throw new InvalidOperationException(
                $"Trace file has {frames.Count} frames, exceeds MaxTraceFrames={_maxTraceFrames}.");

        lock (_framesLock)
        {
            _frames.Clear();
            _frames.AddRange(frames);
            _nextFrameIndex = 0;
            _playStartTimestamp = frames.Count > 0 ? frames[0].Timestamp : -1;
        }

        _logger?.LogInformation("Loaded BLF trace: {FrameCount} frames, first timestamp={Timestamp}s",
            frames.Count, _playStartTimestamp);
    }

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_state == 2, this);

        if (_playStartTimestamp < 0)
            throw new InvalidOperationException("No trace loaded. Call LoadAscii or LoadBlf first.");

        IsConnected = true;
        _playStartWallClock = DateTime.UtcNow;

        // Start timer: immediate first tick, then 1ms interval
        _timer = new System.Threading.Timer(OnTick, null, 0, 1);

        _logger?.LogInformation("TraceDrivenChannel connected, starting playback.");
        return Task.FromResult(Result<Unit>.Ok(default));
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        IsConnected = false;
        _logger?.LogInformation("TraceDrivenChannel disconnected, playback stopped.");
        return Task.CompletedTask;
    }

    // Loopback channel: sent frames become received frames
    private readonly System.Threading.Channels.Channel<CanFrame> _loopbackChannel =
        System.Threading.Channels.Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

    // 线程安全：FrameReceived 可能被两个线程同时 invoke（WriteAsync 测试线程 vs OnTick ThreadPool 线程）
    private readonly object _loopbackLock = new();

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        // Sprint 3: loopback mode — sent frames become received frames
        _loopbackChannel.Writer.TryWrite(frame);

        // 立即同步排空 loopback 帧（不依赖 OnTick）
        // 原因：(1) 避免 OnTick 停止后帧滞留 (2) 测试引擎单线程执行步骤
        ProcessLoopbackInternal();

        return ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    private void ProcessLoopbackInternal()
    {
        lock (_loopbackLock)
        {
            while (_loopbackChannel.Reader.TryRead(out var frame))
            {
                FrameReceived?.Invoke(frame);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 1. 先停止 timer（阻止新的 OnTick 触发）
        _timer?.Dispose();

        // 2. 等待当前正在执行的 OnTick 完成（state 从 1→0）
        SpinWait.SpinUntil(() => Interlocked.Read(ref _state) != 1, 500);

        // 3. 强制设为 Disposing 状态（防止 OnTick 重新进入）
        Interlocked.Exchange(ref _state, 2);

        await Task.CompletedTask;
    }

    private void OnTick(object? state)
    {
        // CAS: only enter if Idle(0), atomically set to CallbackInProgress(1)
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return;

        try
        {
            var elapsedWall = (DateTime.UtcNow - _playStartWallClock).TotalSeconds * _speed;

            // NTP clock jump backward detection
            if (elapsedWall < 0)
            {
                _playStartWallClock = DateTime.UtcNow - TimeSpan.FromSeconds(_playStartTimestamp / _speed);
                elapsedWall = 0;
            }

            var targetTs = _playStartTimestamp + elapsedWall;

            // Collect frames under lock
            lock (_framesLock)
            {
                _emitBuffer.Clear();
                var emitted = 0;
                while (_nextFrameIndex < _frames.Count
                       && _frames[_nextFrameIndex].Timestamp <= targetTs
                       && emitted < _maxFramesPerTick)
                {
                    _emitBuffer.Add(ToCanFrame(_frames[_nextFrameIndex], _id));
                    _nextFrameIndex++;
                    emitted++;
                }
            }

            // Emit outside lock
            var emittedThisTick = _emitBuffer.Count;
            if (emittedThisTick > _maxEmittedPerTick)
                _maxEmittedPerTick = emittedThisTick;

            foreach (var frame in _emitBuffer)
            {
                FrameReceived?.Invoke(frame);
            }

            // Stop timer if all frames emitted
            if (_nextFrameIndex >= _frames.Count)
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                IsConnected = false;
            }
        }
        finally
        {
            // CAS: only set Idle(0) if still CallbackInProgress(1)
            Interlocked.CompareExchange(ref _state, 0, 1);
        }
    }

    private static CanFrame ToCanFrame(ReplayFrame frame, ChannelId channelId)
    {
        var format = (frame.Id > 0x7FFu) ? FrameFormat.Extended : FrameFormat.Standard;
        var totalUs = (ulong)(frame.Timestamp * 1_000_000.0);
        return new CanFrame(
            new CanId(frame.Id, format),
            frame.Data,
            frame.Flags,
            channelId,
            new Timestamp(totalUs));
    }
}
