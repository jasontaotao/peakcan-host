using System.Threading.Channels;

namespace PeakCan.Host.Core.Xcp.Receive;

/// <summary>
/// 内存采集 sink（spec 决策 D4：S2 只需内存 sink + 测试 sink，MDF 归 S4）。
/// 样本与归因双通道各自有界。
/// <para>
/// 满队列策略（定死其一，不留运行时开关）：<b>DropOldest 保最新数据 + 丢帧计数出站</b>。
/// 理由：入队方同步调用、不许阻塞；采集数据的价值在"最新"——用 Wait/DropWrite 会让
/// 消费慢时要么阻塞写者线程要么丢新样本，两头都违背 D4 队列纪律；历史缺口已由归因
/// 通道承接（MissingCause 并入 + 计划空窗状态机）。丢弃必须可见：DroppedValues/
/// DroppedGaps 计数出站，对齐 IFrameSink 先例 RecordService 的 DropOldest + 计数纪律。
/// </para>
/// <para>
/// 实现口径：FullMode.Wait 的 bounded channel + TryWrite 失败时手动淘汰一条再重试 ——
/// 不直接用 FullMode.DropOldest，因为它静默淘汰、无法精确计数。
/// </para>
/// <para>
/// 并发模型（I-2 修订，写者三条是设计面不是异常态）：
/// <list type="bullet">
/// <item>① XcpReceiveLoop 派发线程：OnValues（样本）+ Attributed 转发 → OnGap（逐帧归因）；</item>
/// <item>② PlanGapWatcher 超时回调线程：OnGap（断流归因）；</item>
/// <item>③ RotationScheduler 调用线程：经 watcher 开窗 → OnGap（开窗事件）。</item>
/// </list>
/// 据此 values 通道恒单写者（SingleWriter=true，仅分发线程）；gaps 通道多写者
/// （SingleWriter=false，①②③都会写）。DropOldest 防御分支（腾槽后再写失败 →
/// 放弃本条并计数）在 gaps 多写者下是真实竞争面，不再宣称"常态不可达"；
/// 纪律只保证不阻塞与丢弃可见，不保证极端并发下零丢失。
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

        // values：单写者（仅 XcpReceiveLoop 派发线程）。
        var valueOptions = new BoundedChannelOptions(capacity)
        {
            // Wait + 手动 DropOldest：TryWrite 满即返回 false（不阻塞），
            // 淘汰动作由本类完成以保证丢帧计数精确。
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        };
        _values = Channel.CreateBounded<XcpDaqSample>(valueOptions);

        // gaps：多写者（分发线程/超时回调线程/轮转线程，I-2）。
        var gapOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false,
        };
        _gaps = Channel.CreateBounded<XcpAcquisitionGap>(gapOptions);
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

        // 防御分支：多写者竞争抢走腾出的槽位（gaps 通道三写者下真实可达，I-2）。
        // 放弃本条并计数 —— 纪律只保证不阻塞与丢弃可见，不保证零丢失。
        Interlocked.Increment(ref dropped);
    }
}