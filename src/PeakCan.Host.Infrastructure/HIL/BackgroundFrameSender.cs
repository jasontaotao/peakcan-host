using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// 周期性发送后台 CAN 帧，为被测 ECU 提供正常的总线环境。
/// 每个 BackgroundFrame 对应一个 BackgroundFrameTimer，使用 CAS state machine
/// 防止回调重叠（同 TraceDrivenChannel 模式）。
/// </summary>
public sealed class BackgroundFrameSender : IDisposable
{
    private readonly ICanChannel _channel;
    private readonly List<BackgroundFrameTimer> _timers = new();
    private readonly ILogger? _logger;
    private int _disposed;

    public BackgroundFrameSender(ICanChannel channel, ILogger? logger = null)
    {
        _channel = channel;
        _logger = logger;
    }

    /// <summary>
    /// 启动后台帧发送。校验重复 CAN ID。
    /// </summary>
    public void Start(IReadOnlyList<BackgroundFrame> frames)
    {
        var seenIds = new HashSet<uint>();
        foreach (var f in frames)
        {
            if (!seenIds.Add(f.Id.Raw))
                throw new ArgumentException(
                    $"Duplicate background frame CAN ID: 0x{f.Id.Raw:X} ({f.Id.Format}).", nameof(frames));
            _timers.Add(new BackgroundFrameTimer(_channel, f, _logger));
        }
    }

    /// <summary>
    /// 替换指定 CAN ID 的后台帧数据。找不到时 log warning。
    /// </summary>
    public void UpdateFrameData(CanId id, byte[] newData)
    {
        foreach (var t in _timers)
        {
            if (t.Id.Raw == id.Raw && t.Id.Format == id.Format)
            {
                t.UpdateData(newData);
                return;
            }
        }
        _logger?.LogWarning("UpdateFrameData: background frame 0x{idRaw:X} not found.", id.Raw);
    }

    public void Stop()
    {
        foreach (var t in _timers)
            t.Stop();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var t in _timers)
            t.Dispose();
        _timers.Clear();
    }
}

/// <summary>
/// 单个后台帧的定时发送器。CAS state machine 防止回调重叠。
/// </summary>
internal sealed class BackgroundFrameTimer : IDisposable
{
    private const int Idle = 0;
    private const int CallbackInProgress = 1;
    private const int Disposing = 2;

    private readonly ICanChannel _channel;
    private readonly CanId _id;
    private readonly int _periodMs;
    private readonly bool _fd;
    private readonly FrameFlags _flags;
    private readonly ILogger? _logger;
    // Phase B2: counter/checksum 自动预处理（跨 tick 状态）
    private readonly CounterConfig? _autoCounter;
    private readonly ChecksumConfig? _autoChecksum;
    private ushort _counterValue;
    private volatile byte[] _data;
    private Timer? _timer;
    private long _lastTickTimestamp;
    private int _state = Idle;
    private int _failureCount;

    public CanId Id => _id;

    public BackgroundFrameTimer(ICanChannel channel, BackgroundFrame frame, ILogger? logger = null)
    {
        _channel = channel;
        _id = frame.Id;
        _data = frame.Data;
        _periodMs = frame.PeriodMs;
        _fd = frame.Fd;
        _flags = frame.Fd ? FrameFlags.Fd : FrameFlags.None;
        _logger = logger;
        _autoCounter = frame.AutoCounter;
        _autoChecksum = frame.AutoChecksum;
        // review MEDIUM-4: 首帧输出 StartValue（ApplyCyclic 先增后写；初始化为 StartValue-1 使首帧递增到 StartValue）
        _counterValue = frame.AutoCounter is { } ac ? (ushort)(ac.StartValue - 1) : (ushort)0;
        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _timer = new Timer(OnTick, null, 0, _periodMs);
    }

    public void UpdateData(byte[] newData) => _data = newData;

    private void OnTick(object? state)
    {
        // CAS 进入回调状态
        if (Interlocked.CompareExchange(ref _state, CallbackInProgress, Idle) != Idle)
            return;

        try
        {
            // Late-skip：距上次发送远小于周期一半时跳过
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = (now - _lastTickTimestamp) / (double)Stopwatch.Frequency * 1000;
            if (elapsedMs < _periodMs * 0.5)
                return;

            // Phase B2: counter/checksum 预处理（无配置时不复制，向后兼容）
            var payload = _autoCounter is null && _autoChecksum is null
                ? _data
                : FrameAutoConfigProcessor.ApplyCyclic(_data, _autoCounter, _autoChecksum, ref _counterValue);

            var frame = new CanFrame(
                Id: _id,
                Data: payload,
                Flags: _flags,
                Channel: default,
                Timestamp: default);

            var result = _channel.WriteAsync(frame, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            if (!result.IsSuccess)
            {
                _failureCount++;
                _logger?.LogWarning("BackgroundFrame [{IdRaw:X}] send failed ({Count}): {Error}",
                    _id.Raw, _failureCount, result.Error?.Message);
                if (_failureCount >= 10)
                {
                    _logger?.LogError("BackgroundFrame [{IdRaw:X}] stopped after 10 consecutive failures", _id.Raw);
                    _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            else
            {
                _failureCount = 0;
                _lastTickTimestamp = now;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BackgroundFrame [{IdRaw:X}] OnTick exception", _id.Raw);
        }
        finally
        {
            // CAS：只有当前是 CallbackInProgress 时才设回 Idle，不覆盖 Disposing
            Interlocked.CompareExchange(ref _state, Idle, CallbackInProgress);
        }
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        // 等待正在执行的 OnTick 完成
        SpinWait.SpinUntil(() => Volatile.Read(ref _state) != CallbackInProgress, 500);
    }

    public void Dispose()
    {
        // CAS 标记 Disposing 状态
        if (Interlocked.Exchange(ref _state, Disposing) == Disposing)
            return;
        _timer?.Dispose();
        _timer = null;
    }
}
