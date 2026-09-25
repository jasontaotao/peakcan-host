using System.Collections.Concurrent;
using PeakCan.Host.Core.Xcp.Receive;

namespace PeakCan.Host.App.Services.Xcp;

/// <summary>D3 管线的卡片面板 sink：承接解码样本与归因条目，供 VM 侧 20 Hz 定时批量取走。</summary>
/// <remarks>
/// <para>
/// S2 <c>IXcpAcquisitionSink</c> 队列纪律（照抄 IFrameSink 先例）：<see cref="OnValues"/> /
/// <see cref="OnGap"/> 由接收分发线程、计划空窗状态机线程直接调用，MUST NOT block——
/// 本实现只做无锁入队（ConcurrentQueue + Interlocked 计数），满队列时丢最旧一条（DropOldest），
/// 任何路径都不等待、不阻塞。
/// </para>
/// <para>
/// M-2 异常语义（如实转述）：OnValues 抛异常会被 XcpReceiveLoop 转为 CallbackFailed 归因出站——
/// 异常有归因去向，但样本本身丢失；OnGap 抛异常之上再无归因出口，该条归因被静默丢弃。
/// 本实现<b>不依赖这两条兜底</b>：入队路径只包含对非 null 输入恒不抛的操作
/// （Enqueue / Interlocked / TryDequeue），按构造不抛异常，两条出站口的异常预算留给真正意外。
/// 契约外的 null 输入按"不产生条目"处理（不入队、不抛、不计数）。
/// </para>
/// </remarks>
public sealed class XcpCardPanelSink : IXcpAcquisitionSink
{
    /// <summary>
    /// 默认容量依据：上游 transport DTO 队列默认 1024（"100 Hz × 15 ODT 量级下远超够用"，
    /// XcpCanTransport.DefaultQueueCapacity）。注意 XcpReceiveLoop 是逐 ODT entry 出样本
    ///（T4 评审 MEDIUM）：样本速率 = 100 Hz × entry 总数，多 entry ODT 下成倍高于 DTO 速率
    ///（1500/s 只是"每 ODT 1 entry"口径）。4096 = 上游容量 × 4，与 host 有界队列先例
    ///（transport 1024 / DbcDecode 10000）同量级；20 Hz flush 每 50 ms 消费约 75-600 条。
    /// </summary>
    public const int DefaultCapacity = 4096;

    private readonly ConcurrentQueue<XcpCardPanelEntry> _queue = new();
    private readonly int _capacity;
    private long _count;
    private long _dropped;

    public XcpCardPanelSink(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>DropOldest 丢条计数（values 与 gaps 同队列、同计数；对齐 S2 LocalFrameDrop 归因精神——丢弃必须可见）。</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>当前队列长度（近似值：并发下可瞬时偏离，drain 后守恒）。</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>解码样本入队（接收分发线程调用；不阻塞；按构造不抛，不依赖 CallbackFailed 兜底）。</summary>
    public void OnValues(XcpDaqSample sample)
    {
        if (sample is null)
            return;

        Enqueue(new XcpCardPanelEntry(XcpCardPanelEntryKind.Value, sample, Gap: null));
    }

    /// <summary>归因条目入队（与 OnValues 同队列旁路；不阻塞；按构造不抛，不依赖"静默丢弃"兜底）。</summary>
    public void OnGap(XcpAcquisitionGap gap)
    {
        if (gap is null)
            return;

        Enqueue(new XcpCardPanelEntry(XcpCardPanelEntryKind.Gap, Sample: null, gap));
    }

    /// <summary>
    /// 批量取走至多 <paramref name="maxCount"/> 条（VM 侧 20 Hz DispatcherTimer 驱动调用）。
    /// FIFO 顺序保持（values/gaps 同一队列全局保序）；空队列返回空列表。
    /// </summary>
    public IReadOnlyList<XcpCardPanelEntry> Drain(int maxCount = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        if (maxCount == 0)
            return Array.Empty<XcpCardPanelEntry>();

        List<XcpCardPanelEntry> batch = [];
        while (batch.Count < maxCount && _queue.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _count);
            batch.Add(entry);
        }

        return batch;
    }

    /// <summary>
    /// DropOldest 入队：先入队再判计数，超容量时抽走最旧一条并计数。
    /// 多写者竞争下口径是"不阻塞 + 丢弃可见 + 计数守恒"，与 S2 WriteOldestEvicting 先例同款纪律
    /// （不承诺零丢失、不承诺逐条对位）。
    /// </summary>
    private void Enqueue(XcpCardPanelEntry entry)
    {
        _queue.Enqueue(entry);
        if (Interlocked.Increment(ref _count) <= _capacity)
            return;

        if (_queue.TryDequeue(out _))
        {
            // 抽走的是最旧一条（可能是本条，也可能被并发写者插队——语义仍为丢最旧）。
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
        }
        // TryDequeue 失败（队列瞬时为空）= 本条刚 Enqueue 就被并发 drainer 取走：
        // 本方法的 +1 与 drainer 的 -1 已配平，此处不做任何补偿（T4 评审 HIGH：
        // 原回滚分支是双重扣减，会让 _count 永久漂移为负、有界性腐蚀——已删）。
        // 下一次入队会重新检查容量，有界性自恢复。
    }
}

/// <summary>卡片面板队列条目种类（OnValues 与 OnGap 同队列，条目带类型区分）。</summary>
public enum XcpCardPanelEntryKind
{
    /// <summary>解码样本（来自 OnValues）。</summary>
    Value,

    /// <summary>空窗/断流归因（来自 OnGap，进归因通道）。</summary>
    Gap,
}

/// <summary>卡片面板队列条目：<see cref="Kind"/> 决定 <see cref="Sample"/> 与 <see cref="Gap"/> 哪个非 null。</summary>
public sealed record XcpCardPanelEntry(
    XcpCardPanelEntryKind Kind,
    XcpDaqSample? Sample,
    XcpAcquisitionGap? Gap);
