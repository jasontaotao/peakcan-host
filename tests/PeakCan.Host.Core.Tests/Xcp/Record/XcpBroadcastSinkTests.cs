using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Scheduling;

using System.Globalization;

namespace PeakCan.Host.Core.Tests.Xcp.Record;

/// <summary>
/// S4-T1（spec D4）：广播 sink——S3 会话单 sink 注入下，卡片 sink 与记录 sink 共存。
/// 队列纪律继承：广播层自身无队列、纯转发；子 sink 异常必须隔离（单子故障不拖其他子）。
/// </summary>
public sealed class XcpBroadcastSinkTests
{
    private static readonly PlannedDaqEntry Entry =
        new(Pid: 1, OdtIndex: 0, EntryIndex: 0, ObjectName: "Rpm", SegmentIndex: 0,
            ByteLength: 2, OffsetInOdt: 0, LogicalAddress: 0x1000, PhysicalAddress: null);

    private static XcpDaqSample Sample(double value = 42.0) =>
        new(Entry, value, ReceivedAt: DateTimeOffset.Parse("2026-09-25T08:00:00Z", CultureInfo.InvariantCulture));

    private sealed class RecordingSink : IXcpAcquisitionSink
    {
        public List<XcpDaqSample> Samples { get; } = [];
        public List<XcpAcquisitionGap> Gaps { get; } = [];

        public void OnValues(XcpDaqSample sample) => Samples.Add(sample);

        public void OnGap(XcpAcquisitionGap gap) => Gaps.Add(gap);
    }

    private sealed class ThrowingSink : IXcpAcquisitionSink
    {
        public int ValuesCalls { get; private set; }
        public int GapCalls { get; private set; }

        public void OnValues(XcpDaqSample sample)
        {
            ValuesCalls++;
            throw new InvalidOperationException("record writer exploded");
        }

        public void OnGap(XcpAcquisitionGap gap)
        {
            GapCalls++;
            throw new InvalidOperationException("record writer exploded");
        }
    }

    [Fact]
    public void Broadcasts_values_and_gaps_to_every_child_in_order()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        using var broadcast = new XcpBroadcastSink([first, second]);

        broadcast.OnValues(Sample());
        broadcast.OnGap(XcpAcquisitionGap.AcquisitionInterrupted("down"));

        var sample = Assert.Single(first.Samples);
        Assert.Equal(42.0, sample.Value);
        Assert.Equal("Rpm", sample.Entry.ObjectName);
        Assert.Single(first.Gaps);

        // 顺序广播：两个子 sink 都收到同一条、同一实例引用（纯转发不复制）。
        Assert.Same(sample, Assert.Single(second.Samples));
        Assert.Single(second.Gaps);
    }

    [Fact]
    public void Isolates_throwing_child_without_poisoning_siblings()
    {
        var healthy = new RecordingSink();
        var throwing = new ThrowingSink();
        using var broadcast = new XcpBroadcastSink([healthy, throwing]);

        broadcast.OnValues(Sample());
        broadcast.OnGap(XcpAcquisitionGap.AcquisitionInterrupted("down"));

        // 故障子 sink 抛异常被广播层吸收，健康子 sink 两条都收到。
        Assert.Equal(2, throwing.ValuesCalls + throwing.GapCalls);
        Assert.Single(healthy.Samples);
        Assert.Single(healthy.Gaps);
        Assert.Equal(2, broadcast.ErrorCount);
    }

    [Fact]
    public void Zero_children_is_a_safe_noop()
    {
        using var broadcast = new XcpBroadcastSink([]);

        broadcast.OnValues(Sample());
        broadcast.OnGap(XcpAcquisitionGap.AcquisitionInterrupted("down"));

        Assert.Equal(0, broadcast.ErrorCount);
    }

    [Fact]
    public void Null_children_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new XcpBroadcastSink([null!]));
    }
}


