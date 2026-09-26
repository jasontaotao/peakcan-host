using System.Globalization;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Receive;

namespace PeakCan.Host.Core.Xcp.Record;

/// <summary>
/// S4-T6 触发记录引擎参数（spec D6 / Q2：60 s 硬顶）。
/// </summary>
public sealed class XcpTriggerRecordOptions
{
    /// <summary>触发前窗口秒数（1–60；默认 10）。超 60 配置拒绝（spec Q2）。</summary>
    public int PreTriggerSeconds { get; init; } = 10;

    /// <summary>触发后窗口秒数（1–60；默认 10，与 pre 对称；spec D6 未钉，T8 记实现注记）。</summary>
    public int PostTriggerSeconds { get; init; } = 10;

    /// <summary>估计条率（条/秒；环容量 = pre × rate。60 s × 1500 × 17 B ≈ 1.5 MB 安全量级）。</summary>
    public double EstimatedRatePerSecond { get; init; } = 1500;

    /// <summary>触发文件目录。</summary>
    public string Directory { get; init; } = ".";

    /// <summary>通道清单（缺省值；触发时可经 <see cref="ChannelProvider"/> 按当前关注集给定）。</summary>
    public IReadOnlyList<MdfChannelSpec> Channels { get; init; } = [];

    /// <summary>触发时刻解析通道清单的工厂（关注集运行期动态；null 用 <see cref="Channels"/>）。</summary>
    public Func<IReadOnlyList<MdfChannelSpec>>? ChannelProvider { get; init; }

    /// <summary>契约快照工厂（spec D2：触发文件同样自包含；null = 不落附件）。</summary>
    public Func<ContractSnapshot?>? SnapshotFactory { get; init; }

    /// <summary>写入器工厂 seam（测试注入替身；缺省 Mdf4StreamWriter）。</summary>
    public Func<string, IReadOnlyList<MdfChannelSpec>, IMdfRecordWriter>? WriterFactory { get; init; }

    /// <summary>时间源 seam（测试用；缺省系统时钟）。</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// S4-T6 触发记录引擎（spec D6）：常驻内存环 + 触发落独立 MF4。
/// <para>
/// 常驻环：实现 <see cref="IXcpAcquisitionSink"/> 作广播第三子（卡片 + 记录 + 触发环），
/// 采集运行即写环（锁保护有界队列，满则 DropOldest + <see cref="RingDroppedCount"/> 可见）。
/// 触发事件到达 → 环快照 + 后续流（post 窗口）落 <c>xcp_trigger_{{ts}}.mf4</c>；
/// 主记录文件不被切割（产物形态 D6 定案）。
/// </para>
/// <para>
/// post 窗口关闭：样本到达时间 ≥ 触发时刻 + post 即关窗（该条不入文件）；流停摆兜底 =
/// 墙钟超过窗口 + 60 s 宽限，或 <see cref="CloseCaptureAsync"/>（采集停止先关触发窗）。
/// 采集进行中再触发 → 拒绝 + <see cref="RejectedTriggerCount"/> 可见。
/// </para>
/// </summary>
public sealed class XcpTriggerRecordEngine : IXcpAcquisitionSink, IAsyncDisposable
{
    /// <summary>环条目：样本与 gap 混排（照 XcpMdfRecordSink.RecordItem 先例）。</summary>
    private readonly record struct TriggerItem(XcpDaqSample? Sample, DateTimeOffset GapAt, XcpAcquisitionGap? Gap)
    {
        public DateTimeOffset Timestamp => Sample?.ReceivedAt ?? GapAt;
    }

    private const int CaptureGraceSeconds = 60;

    private readonly XcpTriggerRecordOptions _options;
    private readonly object _lock = new();
    private readonly Queue<TriggerItem> _ring = new();
    private readonly Queue<TriggerItem> _postQueue = new();
    private int _ringCapacity;
    private int _postCapacity;
    private readonly TimeProvider _timeProvider;

    private int _preSeconds;
    private int _postSeconds;

    private bool _capturing;
    private DateTimeOffset _windowEnd;
    private bool _closeRequested;
    private TaskCompletionSource _captureCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _ringDropped;
    private long _postDropped;
    private long _rejectedTriggers;
    private long _unknown;
    private long _captures;
    private bool _faulted;
    private Exception? _lastError;
    private string? _captureFilePath;

    public XcpTriggerRecordEngine(XcpTriggerRecordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PreTriggerSeconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(options), options.PreTriggerSeconds, "PreTriggerSeconds 必须 1–60（60 s 硬顶，spec Q2）");
        if (options.PostTriggerSeconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(options), options.PostTriggerSeconds, "PostTriggerSeconds 必须 1–60");
        if (options.EstimatedRatePerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "EstimatedRatePerSecond 必须 > 0");
        _options = options;
        _preSeconds = options.PreTriggerSeconds;
        _postSeconds = options.PostTriggerSeconds;
        _ringCapacity = Math.Max(1, (int)Math.Ceiling(options.PreTriggerSeconds * options.EstimatedRatePerSecond));
        _postCapacity = Math.Max(1, (int)Math.Ceiling(Math.Max(options.PreTriggerSeconds, options.PostTriggerSeconds) * options.EstimatedRatePerSecond));
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>当前触发前窗口秒数（TrySetWindows 可改）。</summary>
    public int PreTriggerSeconds => _preSeconds;

    /// <summary>当前触发后窗口秒数（TrySetWindows 可改）。</summary>
    public int PostTriggerSeconds => _postSeconds;

    /// <summary>
    /// 运行期改触发窗口（spec D6/Q2：1–60 可配，越界拒绝由调用方在状态区提示）。
    /// 捕获进行中不可改。缩窗时环内最旧条目挤出并计入 <see cref="RingDroppedCount"/>（可见不静默）。
    /// </summary>
    public bool TrySetWindows(int preSeconds, int postSeconds)
    {
        if (preSeconds is < 1 or > 60 || postSeconds is < 1 or > 60)
            return false;
        lock (_lock)
        {
            if (_capturing)
                return false;
            _preSeconds = preSeconds;
            _postSeconds = postSeconds;
            _ringCapacity = Math.Max(1, (int)Math.Ceiling(preSeconds * _options.EstimatedRatePerSecond));
            _postCapacity = Math.Max(1, (int)Math.Ceiling(Math.Max(preSeconds, postSeconds) * _options.EstimatedRatePerSecond));
            while (_ring.Count > _ringCapacity)
            {
                _ring.Dequeue();
                Interlocked.Increment(ref _ringDropped);
            }
        }
        return true;
    }

    /// <summary>是否触发捕获进行中。</summary>
    public bool IsCapturing => _capturing;

    /// <summary>当前/最近一次捕获文件路径（捕获中或完成后非空）。</summary>
    public string? CaptureFilePath => _captureFilePath;

    /// <summary>环满 DropOldest 丢条数（丢弃必须可见）。</summary>
    public long RingDroppedCount => Interlocked.Read(ref _ringDropped);

    /// <summary>post 队列满 DropOldest 丢条数。</summary>
    public long PostDroppedCount => Interlocked.Read(ref _postDropped);

    /// <summary>捕获进行中被拒绝的触发次数。</summary>
    public long RejectedTriggerCount => Interlocked.Read(ref _rejectedTriggers);

    /// <summary>关注集外对象样本条数（路由不到通道，可见不静默）。</summary>
    public long UnknownSampleCount => Interlocked.Read(ref _unknown);

    /// <summary>已完成捕获次数（成功 + 故障关窗）。</summary>
    public long CaptureCount => Interlocked.Read(ref _captures);

    /// <summary>捕获写盘故障（自停当前捕获，状态区红字数据源）。</summary>
    public bool IsFaulted => _faulted;

    public Exception? LastError => _lastError;

    /// <summary>样本入环（广播接收线程调用；无队列阻塞；恒 O(1)）。捕获中同时入 post 队列。</summary>
    public void OnValues(XcpDaqSample sample)
    {
        if (sample is null)
            return;
        Append(new TriggerItem(sample, default, null));
    }

    /// <summary>gap 入环（到达时刻打点，S2 gap 无时间字段——T8 已知限制）。捕获中同时入 post 队列。</summary>
    public void OnGap(XcpAcquisitionGap gap)
    {
        if (gap is null)
            return;
        Append(new TriggerItem(null, DateTimeOffset.UtcNow, gap));
    }

    private void Append(TriggerItem item)
    {
        lock (_lock)
        {
            PushBounded(_ring, item, ref _ringDropped, _ringCapacity);
            if (_capturing)
                PushBounded(_postQueue, item, ref _postDropped, _postCapacity); // T8 评审 P2-2：post 容量独立，post>pre 不丢窗内样本
        }
    }

    private void PushBounded(Queue<TriggerItem> queue, TriggerItem item, ref long dropped, int capacity)
    {
        queue.Enqueue(item);
        if (queue.Count <= capacity)
            return;
        queue.Dequeue();
        Interlocked.Increment(ref dropped);
    }

    /// <summary>
    /// 触发捕获：环快照 + 后续流（post 窗口）落独立 MF4。捕获进行中返回 false（拒绝可见）。
    /// 通道清单 = 触发时刻 ChannelProvider（关注集运行期动态）；空清单拒绝（fail-visible）。
    /// </summary>
    public async Task<bool> TriggerAsync(DateTimeOffset triggerAtUtc, string reason = "manual", CancellationToken ct = default)
    {
        var channels = _options.ChannelProvider?.Invoke() ?? _options.Channels;
        if (channels.Count == 0)
            throw new ArgumentException("通道清单不能为空（关注集为空时不可触发）", nameof(triggerAtUtc));
        if (channels.Count > 64)
            throw new ArgumentException("通道数超写子集上限 64", nameof(triggerAtUtc));

        List<TriggerItem> ringSnapshot;
        lock (_lock)
        {
            if (_capturing)
            {
                Interlocked.Increment(ref _rejectedTriggers);
                return false;
            }
            ringSnapshot = [.. _ring];
            _capturing = true;
            _closeRequested = false;
            _captureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var fileStart = triggerAtUtc.AddSeconds(-_preSeconds);
        _windowEnd = triggerAtUtc.AddSeconds(_postSeconds);
        // T8 评审 P2-3：环快照按时间裁剪到 [fileStart, 触发]——实际条率低于估计时
        // 环内装的是远超 N 秒的数据，按条数落盘会出现过期样本与负相对时间。
        ringSnapshot = [.. ringSnapshot.Where(item => item.Timestamp >= fileStart)];

        var channelIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < channels.Count; i++)
            channelIndexByName.TryAdd(channels[i].Name, i);

        // T8 评审 P1-3：writer 创建/目录准备纳入 try——失败路径必须复位 _capturing
        // 并完成 TCS，否则引擎永久锁死（后续触发全拒 + 采集 Stop 无限等待）。
        IMdfRecordWriter? writer = null;
        try
        {
            System.IO.Directory.CreateDirectory(_options.Directory);
            var name = $"xcp_trigger_{triggerAtUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.mf4";
            _captureFilePath = System.IO.Path.Combine(_options.Directory, name);
            writer = _options.WriterFactory is not null
                ? _options.WriterFactory(_captureFilePath, channels)
                : Mdf4StreamWriter.Create(_captureFilePath, channels, fileStart);

            // D2：触发文件同样自包含——快照在触发时刻导出落 AT 附件。
            var snapshot = _options.SnapshotFactory?.Invoke();
            if (snapshot is { } snap)
            {
                var comment = $"contractSchemaVersion={snap.ContractSchemaVersion}; packageVersion={snap.PackageVersion}";
                await writer.WriteAttachmentAsync(
                    "application/json", comment,
                    System.Text.Encoding.UTF8.GetBytes(ContractSnapshotCodec.Encode(snap)), ct).ConfigureAwait(false);
            }

            // 环快照先行（时间升序 = 到达序），后续流由捕获任务续写。
            foreach (var item in ringSnapshot)
                await WriteItemAsync(writer, channelIndexByName, channels.Count, item, fileStart).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (writer is not null)
            {
                try
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 故障路径收尾异常不再外抛（LastError 已承载首个故障）。
                }
            }
            _faulted = true;
            _lastError = ex;
            Interlocked.Increment(ref _captures);
            lock (_lock)
            {
                _capturing = false;
                _postQueue.Clear();
            }
            _captureCompletion.TrySetResult();
            return true;
        }

        var captureTask = Task.Run(() => CaptureLoopAsync(writer, channelIndexByName, channels.Count, fileStart), CancellationToken.None);
        _ = captureTask.ContinueWith(
            t => _captureCompletion.TrySetResult(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    /// <summary>等待当前捕获收尾（测试/组合根；无捕获即返）。</summary>
    public Task WaitCaptureAsync(CancellationToken ct = default)
    {
        TaskCompletionSource? tcs;
        lock (_lock)
        {
            if (!_capturing)
                return Task.CompletedTask;
            tcs = _captureCompletion;
        }
        return tcs.Task.WaitAsync(ct);
    }

    /// <summary>提前关窗（采集停止先关触发窗；停摆兜底）：排空 post 队列后 Finalize。</summary>
    public async Task CloseCaptureAsync(CancellationToken ct = default)
    {
        // T8 评审 P2-1：锁内绑定本次捕获的 completion——A 完成后新捕获 B 复位
        // _closeRequested/换 TCS 也不会让本次 Close 等 B 的窗口。
        Task completion;
        lock (_lock)
        {
            if (!_capturing)
                return;
            _closeRequested = true;
            completion = _captureCompletion.Task;
        }
        await completion.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task CaptureLoopAsync(IMdfRecordWriter writer, Dictionary<string, int> channelIndexByName,
        int channelCount, DateTimeOffset fileStart)
    {
        try
        {
            var closing = false;
            while (true)
            {
                TriggerItem[] batch;
                lock (_lock)
                {
                    batch = [.. _postQueue];
                    _postQueue.Clear();
                }

                foreach (var item in batch)
                {
                    if (!closing && item.Timestamp >= _windowEnd)
                    {
                        closing = true;
                        break;
                    }
                    await WriteItemAsync(writer, channelIndexByName, channelCount, item, fileStart).ConfigureAwait(false);
                }

                if (closing)
                    break;
                lock (_lock)
                {
                    if (_closeRequested)
                        closing = true;
                }
                if (closing)
                    break;
                if (_timeProvider.GetUtcNow() >= _windowEnd.AddSeconds(CaptureGraceSeconds))
                    break;
                await Task.Delay(10).ConfigureAwait(false);
            }

            await writer.FinalizeAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _captures);
        }
        catch (Exception ex)
        {
            await FaultAsync(writer, ex).ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _capturing = false;
                _postQueue.Clear();
            }
        }
    }

    private async Task WriteItemAsync(IMdfRecordWriter writer, Dictionary<string, int> channelIndexByName,
        int channelCount, TriggerItem item, DateTimeOffset fileStart)
    {
        var seconds = (item.Timestamp - fileStart).TotalSeconds;

        if (item.Gap is { } gap)
        {
            // 会话级归因（spec D3）：事件组一行 + 每样本通道一条失效行。
            await writer.WriteGapEventAsync(
                seconds,
                gap.Kind.ToString(),
                gap.Cause?.ToString() ?? string.Empty,
                gap.Detail,
                gap.ReceiveKind?.ToString() ?? string.Empty,
                gap.ExpectedMaxDuration?.TotalSeconds ?? 0).ConfigureAwait(false);
            for (var i = 0; i < channelCount; i++)
                await writer.WriteInvalidRecordAsync(i, seconds).ConfigureAwait(false);
            return;
        }

        var sample = item.Sample!;
        if (!channelIndexByName.TryGetValue(sample.Entry.ObjectName, out var idx))
        {
            Interlocked.Increment(ref _unknown);
            return;
        }
        await writer.WriteRecordAsync(idx, seconds, sample.Value).ConfigureAwait(false);
    }

    private async Task FaultAsync(IMdfRecordWriter writer, Exception ex)
    {
        _faulted = true;
        _lastError = ex;
        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 故障路径收尾异常不再外抛（LastError 已承载首个故障）。
        }
        Interlocked.Increment(ref _captures);
        lock (_lock)
        {
            _capturing = false;
            _postQueue.Clear();
        }
        _captureCompletion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseCaptureAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _ring.Clear();
            _postQueue.Clear();
        }
    }
}
