using System.Threading.Channels;

namespace PeakCan.Host.Core.Xcp.Receive;

/// <summary>
/// 内存采集 sink（spec 决策 D4：S2 只需内存 sink + 测试 sink，MDF 归 S4）。
/// 样本与归因双通道各自有界。
/// <para>
/// 满队列策略（定死其一，不留运行时开关）：<b>DropOldest 保最新数据 + 丢帧计数出站</b>。
/// 理由：入队方是 XcpReceiveLoop 派发线程（单写者、同步调用、不许阻塞）；
/// 采集数据的价值在"最新"——用 Wait/DropWrite 会让消费慢时要么阻塞接收线程
/// 要么丢新样本，两头都违背 D4 队列纪律；历史缺口已由归因通道承接
/// （MissingCause 并入 + 计划空窗状态机）。丢弃必须可见：DroppedValues/DroppedGaps
/// 计数出站，对齐 IFrameSink 先例 RecordService 的 DropOldest + 计数纪律。
/// </para>
/// <para>
/// 实现口径：FullMode.Wait 的 bounded channel + TryWrite 失败时手动淘汰一条再重试 ——
/// 不直接用 FullMode.DropOldest，因为它静默淘汰、无法精确计数。
/// 并发模型：单写者（XcpReceiveLoop 派发线程，SingleWriter=true）；读者多读安全。
/// 多写者竞争抢走腾出槽位的极端情形按防御分支放弃新条目并计数（仍不阻塞）。
/// </para>
/// </summary>
public sealed class InMemoryAcquisitionSink : IXcpAcquisitionSink
{
    private readonly Channel<XcpDaqSample> _values;
    private readonly Channel<XcpAcquisitionGap> _gaps;
    private long _droppedValues;
    private long _droppedGaps;

    public InMemoryAcquisitionSink(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var options = new BoundedChannelOptions(capacity)
        {
            // Wait + 手动 DropOldest：TryWrite 满即返回 false（不阻塞），
            // 淘汰动作由本类完成以保证丢帧计数精确。
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        };
        _values = Channel.CreateBounded<XcpDaqSample>(options);
        _gaps = Channel.CreateBounded<XcpAcquisitionGap>(options);
    }

    /// <summary>样本读端（消费线程 ReadAllAsync/TryRead）。</summary>
    public ChannelReader<XcpDaqSample> Values => _values.Reader;

    /// <summary>空窗/断流归因读端（消费线程 ReadAllAsync/TryRead）。</summary>
    public ChannelReader<XcpAcquisitionGap> Gaps => _gaps.Reader;

    /// <summary>满队列 DropOldest 丢弃的样本计数（丢弃不静默）。</summary>
    public long DroppedValues => Interlocked.Read(ref _droppedValues);

    /// <summary>满队列 DropOldest 丢弃的归因计数（丢弃不静默）。</summary>
    public long DroppedGaps => Interlocked.Read(ref _droppedGaps);

    public void OnValues(XcpDaqSample sample) =>
        WriteOldestEvicting(_values, sample, ref _droppedValues);

    public void OnGap(XcpAcquisitionGap gap) =>
        WriteOldestEvicting(_gaps, gap, ref _droppedGaps);

    /// <summary>满队列 DropOldest 写入（同步、不阻塞）：腾一格保最新，计数兜底。</summary>
    private static void WriteOldestEvicting<T>(Channel<T> channel, T item, ref long dropped)
    {
        if (channel.Writer.TryWrite(item))
            return;

        // 满队列：丢最旧一条，保最新数据。
        if (channel.Reader.TryRead(out _))
            Interlocked.Increment(ref dropped);

        if (channel.Writer.TryWrite(item))
            return;

        // 防御分支：多写者竞争抢走腾出的槽位（契约单写者，常态不可达）。
        // 放弃本条并计数 —— 纪律只保证不阻塞，不保证极端并发下零丢失。
        Interlocked.Increment(ref dropped);
    }
}