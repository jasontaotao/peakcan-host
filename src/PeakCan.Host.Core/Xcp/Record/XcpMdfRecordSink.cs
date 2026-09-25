using System.Collections.Concurrent;
using System.Globalization;
using PeakCan.Host.Core.Xcp.Receive;

namespace PeakCan.Host.Core.Xcp.Record;

/// <summary>S4-T2 记录 sink 参数（spec D2/D5）。</summary>
public sealed class XcpMdfRecordSinkOptions
{
    /// <summary>有界队列容量（DropOldest；对齐卡片 sink 先例，4096 ≈ 上游容量 × 4）。</summary>
    public int QueueCapacity { get; init; } = 4096;

    /// <summary>记录文件目录（缺省当前目录；组合根接会话目录）。</summary>
    public string Directory { get; init; } = ".";

    /// <summary>通道清单（= 关注集；顺序即 DG 序，对象名路由键）。</summary>
    public IReadOnlyList<MdfChannelSpec> Channels { get; init; } = [];

    /// <summary>文件名前缀（缺省 xcp；触发记录 S4-T6 用独立前缀）。</summary>
    public string FilePrefix { get; init; } = "xcp";

    /// <summary>写入器工厂 seam（测试注入故障/替身；缺省 Mdf4StreamWriter）。</summary>
    public Func<string, IReadOnlyList<MdfChannelSpec>, IMdfRecordWriter>? WriterFactory { get; init; }
}

/// <summary>
/// S4-T2 MDF 记录 sink（spec D2/D3/D5）：承接 S3 广播 sink 的样本流落 MF4。
/// <para>
/// 队列纪律（照 XcpCardPanelSink 先例）：OnValues 无锁入队恒不阻塞，满队列 DropOldest +
/// <see cref="DroppedCount"/> 可见；重活（IO）由后台消费线程承担。
/// </para>
/// <para>
/// 生命周期：未 Start / 已 Stop 的 OnValues 无操作幂等；<see cref="StopAsync"/> 先排空队列
/// 再 Finalize（尾部样本不丢）；写线程异常 → <see cref="IsFaulted"/> + <see cref="LastError"/>
/// + 记录自停，采集不受影响（spec D5 故障隔离；广播层已隔离对卡片管线的毒化）。
/// </para>
/// </summary>
public sealed class XcpMdfRecordSink : IXcpAcquisitionSink, IAsyncDisposable
{
    private readonly XcpMdfRecordSinkOptions _options;
    private readonly ConcurrentQueue<XcpDaqSample> _queue = new();
    private readonly Dictionary<string, int> _channelIndexByName;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IMdfRecordWriter? _writer;
    private Task? _consumer;
    private CancellationTokenSource? _consumerCts;
    private long _written;
    private long _dropped;
    private long _unknown;
    private volatile bool _stopping;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _stoppedUtc;

    public XcpMdfRecordSink(XcpMdfRecordSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.QueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "QueueCapacity 必须 ≥ 1");
        if (options.Channels.Count == 0)
            throw new ArgumentException("通道清单不能为空", nameof(options));
        _options = options;
        _channelIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < options.Channels.Count; i++)
            _channelIndexByName.TryAdd(options.Channels[i].Name, i);
    }

    /// <summary>记录文件全路径（Start 后非空）。</summary>
    public string? FilePath { get; private set; }

    /// <summary>是否记录中。</summary>
    public bool IsRecording { get; private set; }

    /// <summary>写入器故障（记录自停；状态区红字数据源，spec D5）。</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>最近一次写入器故障（故障后非空）。</summary>
    public Exception? LastError { get; private set; }

    /// <summary>成功落盘条数。</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>满队列 DropOldest 丢条数。</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>未知对象样本条数（关注集外对象——路由不到通道，可见不静默）。</summary>
    public long UnknownSampleCount => Interlocked.Read(ref _unknown);

    /// <summary>记录时长（Stop 后为终值；记录中为已持续时长）。</summary>
    public TimeSpan Duration => _startedUtc is null
        ? TimeSpan.Zero
        : (_stoppedUtc ?? DateTimeOffset.UtcNow) - _startedUtc.Value;

    /// <summary>开始记录：落元数据块 + 启动后台写线程。</summary>
    public async Task StartAsync(DateTimeOffset startTimeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(_options.Directory);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRecording)
                throw new InvalidOperationException("记录已在进行");
            if (IsFaulted)
                throw new InvalidOperationException("写入器已故障，不可重启（创建新 sink）");

            System.IO.Directory.CreateDirectory(_options.Directory);
            var name = $"{_options.FilePrefix}_{startTimeUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.mf4";
            FilePath = System.IO.Path.Combine(_options.Directory, name);

            if (_options.WriterFactory is not null)
                _writer = _options.WriterFactory(FilePath, _options.Channels);
            else
                _writer = Mdf4StreamWriter.Create(FilePath, _options.Channels, startTimeUtc);

            _startedUtc = startTimeUtc;
            _stoppedUtc = null;
            _stopping = false;
            IsRecording = true;
            _consumerCts = new CancellationTokenSource();
            _consumer = Task.Run(() => ConsumeLoopAsync(_consumerCts.Token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>解码样本入队（接收分发线程调用；不阻塞；未记录态无操作）。</summary>
    public void OnValues(XcpDaqSample sample)
    {
        if (sample is null || !IsRecording || _stopping)
            return;

        _queue.Enqueue(sample);
        if (Interlocked.Increment(ref _queueCount) <= _options.QueueCapacity)
            return;

        if (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queueCount);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>归因条目（S4-T4 落盘；本任务暂不入文件）。</summary>
    public void OnGap(XcpAcquisitionGap gap)
    {
        // T4 接管：invalidation bits + 归因事件组。
    }

    /// <summary>停止记录：排空队列 → Finalize → 关闭（幂等；故障路径吞 Finalize 异常进 LastError）。</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRecording)
                return;

            _stopping = true;
            try
            {
                if (_consumer is not null)
                    await _consumer.ConfigureAwait(false);
                if (_writer is not null && !IsFaulted)
                    await _writer.FinalizeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IsFaulted = true;
                LastError = ex;
            }
            finally
            {
                _stoppedUtc = DateTimeOffset.UtcNow;
                IsRecording = false;
                _consumerCts?.Dispose();
                _consumerCts = null;
                _consumer = null;
                if (_writer is not null)
                {
                    await _writer.DisposeAsync();
                    _writer = null;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>等价 StopAsync（组合根/关闭路径友好）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (IsRecording)
            await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private long _queueCount;

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var drained = DrainOnce();
                if (_stopping && _queueCount == 0)
                    return;
                if (!drained)
                    await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsync 的 consumer await 正常收尾路径
        }
        catch (Exception ex)
        {
            IsFaulted = true;
            LastError = ex;
        }
    }

    private bool DrainOnce()
    {
        var drained = false;
        while (_queue.TryDequeue(out var sample))
        {
            Interlocked.Decrement(ref _queueCount);
            drained = true;

            if (_writer is null || IsFaulted)
                continue;

            if (!_channelIndexByName.TryGetValue(sample.Entry.ObjectName, out var idx))
            {
                Interlocked.Increment(ref _unknown);
                continue;
            }

            var seconds = (sample.ReceivedAt - _startedUtc!.Value).TotalSeconds;
            _writer.WriteRecordAsync(idx, seconds, sample.Value).GetAwaiter().GetResult();
            Interlocked.Increment(ref _written);
        }

        return drained;
    }
}




