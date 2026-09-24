using System.Diagnostics;
using System.Threading.Channels;
using A2lEditor.Core.Layout;
using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Receive;

/// <summary>
/// S2-T15：sink 抽象 + 归因生产（spec §3 Receive 空窗归因写死条款 + 决策 D4）。
/// <para>
/// 覆盖矩阵：(a) 入队不阻塞 + 满队列 DropOldest 保新数据 + 丢帧计数出站；
/// (b) AcquisitionInterrupted 由 Receive 层生产（包枚举槽位首次有生产者）；
/// (c) 计划空窗：PlanGapWindow 携带预期时长上界，期内样本恢复关窗 / 超时升级断流
/// （FakeTimeProvider 驱动）；(d) 包 MissingCause 五值并入通道全通过；
/// (e) 重配细分归 host，不改包枚举（API 面审计断言）。
/// </para>
/// </summary>
public class SinkTests
{
    private static readonly string[] PinnedMissingCauseNames =
    [
        "NotAcquired",
        "SegmentMissing",
        "ConversionUnsupported",
        "AccessBlocked",
        "AccessInferred",
        "AcquisitionInterrupted",
    ];

    // ------------------------------------------------------------------
    // (a) 入队不阻塞：满队列 DropOldest（定死策略）+ 计数
    // ------------------------------------------------------------------

    [Fact]
    public void OnValues_FullQueue_DropOldest_Keeps_Newest_Never_Blocks_And_Counts_Drops()
    {
        var sink = new InMemoryAcquisitionSink(capacity: 2);
        var watch = Stopwatch.StartNew();

        // 无消费者在场连写 5 条：Wait/TryWrite 阻塞型实现在这里会挂死测试，
        // 本断言既是策略断言（保最新两条）也是"绝不阻塞接收线程"的硬证据。
        for (var i = 0; i < 5; i++)
            sink.OnValues(Sample(value: i));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1),
            $"sink enqueue blocked the caller for {watch.Elapsed.TotalMilliseconds:F0} ms");
        double[] expectedSurvivors = [3d, 4d];
        Assert.Equal(expectedSurvivors, Drain(sink.Values).Select(s => s.Value).ToArray());
        Assert.Equal(3, sink.DroppedValues);
        Assert.Equal(0, sink.DroppedGaps);
    }

    [Fact]
    public void OnGap_FullQueue_DropOldest_Keeps_Newest_And_Counts_Drops()
    {
        var sink = new InMemoryAcquisitionSink(capacity: 1);

        sink.OnGap(Gap(1));
        sink.OnGap(Gap(2));

        var drained = Drain(sink.Gaps);
        var gap = Assert.Single(drained);
        Assert.Contains("2", gap.Detail);
        Assert.Equal(1, sink.DroppedGaps);
    }

    // ------------------------------------------------------------------
    // (b) AcquisitionInterrupted 由 Receive 层生产（包枚举槽位首次有生产者）
    // ------------------------------------------------------------------

    [Fact]
    public void PlanGap_Timeout_Produces_AcquisitionInterrupted_With_Package_Enum_Value()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(16);
        using var watcher = new PlanGapWatcher(sink, clock);

        watcher.OnPlanGapWindow(new RotationGapWindow(OdtCount: 3, EntryCount: 10,
            ExpectedMaxDuration: TimeSpan.FromMilliseconds(500)));
        clock.Advance(TimeSpan.FromMilliseconds(500));

        var gaps = Drain(sink.Gaps);
        Assert.Equal(2, gaps.Count);
        Assert.Equal(XcpAcquisitionGapKind.PlanGapOpened, gaps[0].Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(500), gaps[0].ExpectedMaxDuration);
        Assert.Equal(XcpAcquisitionGapKind.AcquisitionInterrupted, gaps[1].Kind);
        // 枚举值来自包侧（A2lEditor.Core 槽位），由本层（Receive 状态机）写入。
        Assert.Equal(MissingCause.AcquisitionInterrupted, gaps[1].Cause);
        Assert.Equal("A2lEditor.Core", typeof(MissingCause).Assembly.GetName().Name);
        Assert.False(watcher.IsOpen);
    }

    // ------------------------------------------------------------------
    // (c) 计划空窗状态机：期内恢复关窗 / 超时升级断流 / 新窗取代旧窗
    // ------------------------------------------------------------------

    [Fact]
    public void PlanGap_Sample_Within_Window_Closes_Window_Without_Interrupt()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(16);
        using var watcher = new PlanGapWatcher(sink, clock);

        watcher.OnPlanGapWindow(new RotationGapWindow(3, 10, TimeSpan.FromMilliseconds(500)));
        clock.Advance(TimeSpan.FromMilliseconds(499));
        watcher.OnSampleReceived(Sample());
        clock.Advance(TimeSpan.FromSeconds(10));

        // 只有开窗事件；期内恢复不产断流归因，且超时定时器已取消。
        var gaps = Drain(sink.Gaps);
        var opened = Assert.Single(gaps);
        Assert.Equal(XcpAcquisitionGapKind.PlanGapOpened, opened.Kind);
        Assert.False(watcher.IsOpen);
    }

    [Fact]
    public void PlanGap_New_Window_Supersedes_Open_Window()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(16);
        using var watcher = new PlanGapWatcher(sink, clock);

        watcher.OnPlanGapWindow(new RotationGapWindow(1, 1, TimeSpan.FromMilliseconds(500)));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        watcher.OnPlanGapWindow(new RotationGapWindow(2, 2, TimeSpan.FromMilliseconds(500)));
        clock.Advance(TimeSpan.FromMilliseconds(99));

        // 499ms 时刻：旧窗原定 500ms 到期，但已被新窗取代——不得升级断流。
        Assert.Equal(2, sink.Gaps.Count);
        clock.Advance(TimeSpan.FromMilliseconds(401));
        Assert.Equal(3, sink.Gaps.Count);
        var last = PeekLast(sink.Gaps);
        Assert.Equal(XcpAcquisitionGapKind.AcquisitionInterrupted, last.Kind);
    }

    // ------------------------------------------------------------------
    // 接线：T14 回调出站口 → sink（回调契约不回退：委托形状原样转发）
    // ------------------------------------------------------------------

    [Fact]
    public void SampleDecoded_Wiring_Closes_PlanGap_Then_Enqueues_Value()
    {
        var clock = new FakeTimeProvider();
        var sink = new InMemoryAcquisitionSink(16);
        using var watcher = new PlanGapWatcher(sink, clock);
        var sampleDecoded = XcpAcquisitionSinkWiring.SampleDecoded(sink, watcher);

        watcher.OnPlanGapWindow(new RotationGapWindow(3, 10, TimeSpan.FromMilliseconds(500)));
        sampleDecoded(Sample(value: 42));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(watcher.IsOpen);
        var values = Drain(sink.Values);
        var value = Assert.Single(values);
        Assert.Equal(42, value.Value);
        Assert.Equal(1, sink.Gaps.Count); // 只有开窗事件，无断流
    }

    // ------------------------------------------------------------------
    // (d) 包 MissingCause 五值并入通道（全通过）
    // ------------------------------------------------------------------

    [Fact]
    public void Package_MissingCause_Five_Values_Flow_Into_Gap_Channel()
    {
        var sink = new InMemoryAcquisitionSink(16);
        var attributed = XcpAcquisitionSinkWiring.Attributed(sink);
        MissingCause[] five =
        [
            MissingCause.NotAcquired,
            MissingCause.SegmentMissing,
            MissingCause.ConversionUnsupported,
            MissingCause.AccessBlocked,
            MissingCause.AccessInferred,
        ];

        foreach (var cause in five)
            attributed(new XcpReceiveAttribution(XcpReceiveAttributionKind.DecodeFailed, 0x00, $"cause {cause}", cause));

        var gaps = Drain(sink.Gaps);
        Assert.Equal(5, gaps.Count);
        Assert.All(gaps, g => Assert.Equal(XcpAcquisitionGapKind.MissingCauseAttributed, g.Kind));
        // M-3：帧级 Kind 不并入 Detail 糊掉——独立字段保留。
        Assert.All(gaps, g => Assert.Equal(XcpReceiveAttributionKind.DecodeFailed, g.ReceiveKind));
        Assert.Equal(five, gaps.Select(g => g.Cause!.Value).ToArray());

        // 无包侧 cause 的逐帧归因也过通道（spec 定为并入，不丢 kind 信息之外的事实）。
        attributed(new XcpReceiveAttribution(XcpReceiveAttributionKind.MalformedFrame, 0x00, "short dto"));
        var passthrough = Drain(sink.Gaps);
        var gap = Assert.Single(passthrough);
        Assert.Equal(XcpAcquisitionGapKind.ReceiveAttribution, gap.Kind);
        Assert.Null(gap.Cause);
        Assert.Equal(XcpReceiveAttributionKind.MalformedFrame, gap.ReceiveKind);
        Assert.Equal("short dto", gap.Detail);
    }

    // ------------------------------------------------------------------
    // (e) 重配细分归 host，不改包枚举 —— API 面审计断言
    // ------------------------------------------------------------------

    [Fact]
    public void Package_MissingCause_Enum_API_Surface_Unchanged_No_Local_Clone()
    {
        // 宾语侧守卫同型（XcpLayeringTests 先例）：包侧重命名/扩员时这里先炸，
        // 而不是静默通过。成员集合逐名钉死 = 写死条款"不改包枚举"的 API 面。
        Assert.Equal("A2lEditor.Core", typeof(MissingCause).Assembly.GetName().Name);
        Assert.Equal(PinnedMissingCauseNames, Enum.GetNames<MissingCause>());

        // 本层不平行再造包枚举：Core 程序集不得声明名为 MissingCause 的枚举，
        // 也不得在包侧命名空间下声明任何类型（防第二套语义分叉）。
        Assert.DoesNotContain(typeof(PlanGapWatcher).Assembly.GetTypes(),
            t => t.IsEnum && t.Name == "MissingCause");
        Assert.DoesNotContain(typeof(PlanGapWatcher).Assembly.GetTypes(),
            t => t.Namespace == typeof(MissingCause).Namespace);
    }

    // ------------------------------------------------------------------
    // I-1 回归：被取代窗口的残留定时器回调不得错杀新窗（真实线程 + 门闩）
    // ------------------------------------------------------------------

    [Fact]
    public void Stale_Timer_Callback_Does_Not_Kill_Superseded_Window()
    {
        var provider = new LatchedTimeProvider();
        var sink = new InMemoryAcquisitionSink(16);
        using var watcher = new PlanGapWatcher(sink, provider);

        // 窗 A：旧回调由 Fire() 拉起在真实线程上，先被门闩按在进入状态机临界区之前
        // （与"阻塞在 _gate"等价的先行关系，且确定性——不靠 sleep 碰运气）。
        watcher.OnPlanGapWindow(new RotationGapWindow(1, 1, TimeSpan.FromMilliseconds(50)));
        var timerA = provider.Timers[0];
        timerA.Fire();

        // A 的回调被按住期间，主线程完整跑完 Open(B)（新窗落位 + 新定时器武装）。
        watcher.OnPlanGapWindow(new RotationGapWindow(2, 2, TimeSpan.FromMilliseconds(50)));
        var timerB = provider.Timers[1];

        // 放行旧回调：旧代际 state 与当班窗 B 不匹配 → 不得宣判断流、不得销毁 B 的定时器。
        timerA.Release.Set();
        Assert.NotNull(timerA.Worker);
        Assert.True(timerA.Worker!.Join(TimeSpan.FromSeconds(5)), "stale callback did not finish");

        Assert.False(timerB.Disposed, "stale callback destroyed the live window's timer");
        Assert.True(watcher.IsOpen);
        var gaps = Drain(sink.Gaps);
        Assert.DoesNotContain(gaps, g => g.Kind == XcpAcquisitionGapKind.AcquisitionInterrupted);
        Assert.Equal(2, gaps.Count); // 两次开窗事件，无任何断流
    }

    // ------------------------------------------------------------------
    // 夹具
    // ------------------------------------------------------------------

    /// <summary>I-1 门闩定时器：Fire() 在真实线程上拉起回调，Release 门闩控制续跑时序。</summary>
    private sealed class LatchedTimer(TimerCallback callback, object? state) : ITimer
    {
        public ManualResetEventSlim Release { get; } = new(false);
        public Thread? Worker { get; private set; }
        public bool Disposed { get; private set; }

        public void Fire()
        {
            Worker = new Thread(() =>
            {
                Release.Wait();
                callback(state);
            })
            { IsBackground = true };
            Worker.Start();
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>记录 CreateTimer 产物的 TimeProvider 替身（I-1 时序控制用）。</summary>
    private sealed class LatchedTimeProvider : TimeProvider
    {
        public List<LatchedTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new LatchedTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }
    }

    private static XcpDaqSample Sample(string objectName = "D0", double value = 1d) =>
        new(new PlannedDaqEntry(
                Pid: 0, OdtIndex: 0, EntryIndex: 0, ObjectName: objectName, SegmentIndex: 0,
                ByteLength: 4, OffsetInOdt: 0, LogicalAddress: 0x1000, PhysicalAddress: 0x90001000),
            value);

    private static XcpAcquisitionGap Gap(int marker) =>
        new(XcpAcquisitionGapKind.ReceiveAttribution, $"gap {marker}");

    private static IReadOnlyList<T> Drain<T>(ChannelReader<T> reader)
    {
        var items = new List<T>();
        while (reader.TryRead(out var item))
            items.Add(item);
        return items;
    }

    private static T PeekLast<T>(ChannelReader<T> reader) where T : notnull
    {
        var items = Drain(reader);
        return items[^1];
    }
}