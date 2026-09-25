using System.Diagnostics;
using PeakCan.Host.App.Services.Xcp;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.App.Tests.Services.Xcp;

/// <summary>
/// S3-T4：XcpCardPanelSink（spec D3 管线）。
/// <para>
/// 覆盖矩阵：(a) 入队不阻塞（10 万样本灌入远快于阻塞型实现）；
/// (b) 满队列 DropOldest（最旧丢、计数出站、容量不变）；
/// (c) drain 批量取走（FIFO 保持 + 空队列返回空）；
/// (d) OnGap/OnValues 同队列交错出入队顺序；
/// (e) 非法输入不炸 sink（M-2：不依赖接收层兜底，本实现按构造不抛）。
/// </para>
/// </summary>
public class XcpCardPanelSinkTests
{
    // ------------------------------------------------------------------
    // (a) 入队不阻塞
    // ------------------------------------------------------------------

    [Fact]
    public void OnValues_HundredThousandSamples_Never_Blocks_The_Caller()
    {
        var sink = new XcpCardPanelSink(capacity: 1024);
        var watch = Stopwatch.StartNew();

        // 无消费者在场连灌 10 万条：阻塞型有界队列（WaitToWriteAsync 同步等待 /
        // lock 信号量）在这里会挂死测试。计时断言是"绝不阻塞接收分发线程"的硬证据。
        for (var i = 0; i < 100_000; i++)
            sink.OnValues(Sample(value: i));
        watch.Stop();

        // T4 评审 LOW：1s 上限在 CI 高负载下有 flaky 风险，放宽到 10s——
        // 阻塞型实现是"挂死"而非"慢"，10s 上限仍保有检错力。
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"sink enqueue blocked the caller for {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(1024, sink.Count);
        Assert.Equal(100_000 - 1024, sink.DroppedCount);
    }

    // ------------------------------------------------------------------
    // (b) 满队列 DropOldest
    // ------------------------------------------------------------------

    [Fact]
    public void OnValues_FullQueue_Drops_Oldest_Counts_Drops_Keeps_Capacity()
    {
        var sink = new XcpCardPanelSink(capacity: 3);

        for (var i = 0; i < 5; i++)
            sink.OnValues(Sample(value: i));

        var drained = sink.Drain();
        Assert.Equal([2d, 3d, 4d], drained.Select(e => e.Sample!.Value).ToArray());
        Assert.Equal(2, sink.DroppedCount);
        Assert.Equal(0, sink.Count);
    }

    // ------------------------------------------------------------------
    // (c) drain 批量取走
    // ------------------------------------------------------------------

    [Fact]
    public void Drain_Takes_Batch_In_Fifo_Order_And_Empty_Queue_Returns_Empty()
    {
        var sink = new XcpCardPanelSink(capacity: 8);
        for (var i = 0; i < 4; i++)
            sink.OnValues(Sample(objectName: $"obj{i}", value: i));

        var first = sink.Drain(maxCount: 2);
        Assert.Equal(2, first.Count);
        Assert.Equal("obj0", first[0].Sample!.Entry.ObjectName);
        Assert.Equal("obj1", first[1].Sample!.Entry.ObjectName);
        Assert.Equal(2, sink.Count);

        var rest = sink.Drain();
        Assert.Equal(2, rest.Count);
        Assert.Equal("obj2", rest[0].Sample!.Entry.ObjectName);
        Assert.Equal("obj3", rest[1].Sample!.Entry.ObjectName);

        var empty = sink.Drain();
        Assert.Empty(empty);
        Assert.Equal(0, sink.Count);
    }

    // ------------------------------------------------------------------
    // (d) OnGap / OnValues 同队列交错
    // ------------------------------------------------------------------

    [Fact]
    public void OnValues_And_OnGap_Interleave_Into_One_Queue_Keeps_Global_Order()
    {
        var sink = new XcpCardPanelSink(capacity: 16);

        sink.OnValues(Sample(objectName: "v0", value: 0));
        sink.OnGap(Gap(1));
        sink.OnValues(Sample(objectName: "v2", value: 2));
        sink.OnGap(Gap(3));

        var drained = sink.Drain();
        Assert.Equal(
            [XcpCardPanelEntryKind.Value, XcpCardPanelEntryKind.Gap,
             XcpCardPanelEntryKind.Value, XcpCardPanelEntryKind.Gap],
            drained.Select(e => e.Kind).ToArray());
        Assert.Equal("v0", drained[0].Sample!.Entry.ObjectName);
        Assert.Equal("gap 1", drained[1].Gap!.Detail);
        Assert.Equal("v2", drained[2].Sample!.Entry.ObjectName);
        Assert.Equal("gap 3", drained[3].Gap!.Detail);
    }

    [Fact]
    public void OnGap_FullQueue_DropOldest_Counts_Drops_Like_Values()
    {
        var sink = new XcpCardPanelSink(capacity: 1);

        sink.OnGap(Gap(1));
        sink.OnGap(Gap(2));

        var drained = sink.Drain();
        var gap = Assert.Single(drained);
        Assert.Equal(XcpCardPanelEntryKind.Gap, gap.Kind);
        Assert.Contains("2", gap.Gap!.Detail);
        Assert.Equal(1, sink.DroppedCount);
    }

    // ------------------------------------------------------------------
    // (e) M-2 转述：不依赖接收层兜底，实现按构造不抛
    // ------------------------------------------------------------------

    [Fact]
    public void OnValues_And_OnGap_Null_Input_Does_Not_Throw_And_Sink_Stays_Usable()
    {
        var sink = new XcpCardPanelSink(capacity: 4);

        // M-2：OnValues 抛异常 → 接收层转 CallbackFailed（样本丢失）；OnGap 抛异常 →
        // 该条归因静默丢弃。本实现不依赖这两条兜底：入队路径对任何输入都不抛，
        // 契约外的 null 输入按"不产生条目"处理，sink 自身保持可用。
        sink.OnValues(null!);
        sink.OnGap(null!);
        Assert.Equal(0, sink.Count);

        sink.OnValues(Sample(value: 42));
        var drained = sink.Drain();
        Assert.Equal(42d, Assert.Single(drained).Sample!.Value);
    }

    [Fact]
    public async Task Concurrent_Producers_And_Drainers_Keep_Accounting_Invariant()
    {
        const int total = 100_000;
        const int capacity = 1024;
        var sink = new XcpCardPanelSink(capacity);
        var producersDone = 0;

        // 生产者 4 × 25k 与消费者 2 并发：守恒式（灌入 = 已取走 + 残余 + 丢弃）
        // 数学上恒真，真正的检错断言是末尾 Count == 0（T4 评审 LOW：曾能捕捉
        // Enqueue 回滚分支的计数漂移 flaky——该缺陷已修，本测试留作回归哨兵）。
        var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < total / 4; i++)
                sink.OnValues(Sample(value: i));
        })).ToArray();

        var drainers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            long taken = 0;
            while (true)
            {
                taken += sink.Drain(maxCount: 256).Count;
                if (Volatile.Read(ref producersDone) == 1 && sink.Count == 0)
                    return taken;
                if (sink.Count == 0)
                    await Task.Yield(); // 队列暂空且生产未完：让出线程，不烧 CPU
            }
        })).ToArray();

        await Task.WhenAll(producers);
        Volatile.Write(ref producersDone, 1);
        var drainedByWorkers = (await Task.WhenAll(drainers)).Sum();

        // 收尾一次全量 drain，兜住 drainer 退出判定与最后一次入队之间的竞态窗口。
        var finalDrain = sink.Drain().Count;

        Assert.Equal(0, sink.Count);
        Assert.Equal(total, drainedByWorkers + finalDrain + sink.DroppedCount);
    }

    // ------------------------------------------------------------------
    // 测试构造 helpers（照抄 Core SinkTests 先例的 PlannedDaqEntry 形状）
    // ------------------------------------------------------------------

    private static XcpDaqSample Sample(string objectName = "D0", double value = 1d) =>
        new(new PlannedDaqEntry(
                Pid: 0, OdtIndex: 0, EntryIndex: 0, ObjectName: objectName, SegmentIndex: 0,
                ByteLength: 4, OffsetInOdt: 0, LogicalAddress: 0x1000, PhysicalAddress: 0x90001000),
            value,
            ReceivedAt: default);

    private static XcpAcquisitionGap Gap(int marker) =>
        new(XcpAcquisitionGapKind.ReceiveAttribution, $"gap {marker}");
}

