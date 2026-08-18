using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.Tests.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.StepExecutor;

/// <summary>
/// Phase B: FrameStatisticsCollector + 3 个帧时序断言 executor 测试。
/// 注入可控时钟（TestClock）精确控制帧时间戳；FakeCanChannel.WriteAsync loopback 喂帧。
/// executor 不访问 IAssertionContext（仅依赖 IFrameStatistics），ctx 传 null。
/// </summary>
public class FrameStatisticsExecutorTests
{
    private static readonly CanId Id = new(0x123, FrameFormat.Standard);
    private static readonly CanId OtherId = new(0x456, FrameFormat.Standard);

    private sealed class TestClock
    {
        public long Ticks = 1_000_000;
        public long Now() => Ticks;
    }

    private static CanFrame MakeFrame(CanId id)
        => new(id, new byte[] { 0x01 }, FrameFlags.None, default, default);

    private static async Task<(FakeCanChannel Channel, FrameStatisticsCollector Collector, TestClock Clock)> SetupAsync()
    {
        var clock = new TestClock();
        var channel = new FakeCanChannel();
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        var collector = new FrameStatisticsCollector(channel, clock.Now);
        return (channel, collector, clock);
    }

    // ---- FrameStatisticsCollector ----

    [Fact]
    public async Task Collector_CountSince_CountsInWindow()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            await channel.WriteAsync(MakeFrame(Id));          // t = 1_000_000
            clock.Ticks += 100;
            await channel.WriteAsync(MakeFrame(Id));          // 1_000_100
            clock.Ticks += 100;
            await channel.WriteAsync(MakeFrame(Id));          // 1_000_200

            collector.CountSince(Id, 1_000_050, 1_000_200).Should().Be(2);   // 1_000_100, 1_000_200
            collector.CountSince(Id, 1_000_201, 1_000_200).Should().Be(0);
            collector.CountSince(OtherId, 0, 1_000_200).Should().Be(0);      // 不同 ID 互不影响
        }
        finally { channel.Dispose(); }
    }

    [Fact]
    public async Task Collector_WindowExpiry_EvictsStaleFrames()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            await channel.WriteAsync(MakeFrame(Id));          // t = 1_000_000
            clock.Ticks = 2_000_000;                          // 远超 RetentionMs(5s)

            collector.CountSince(Id, 1_999_900, 2_000_000).Should().Be(0);  // 旧帧被懒淘汰
        }
        finally { channel.Dispose(); }
    }

    [Fact]
    public async Task Collector_QueueCap_DoesNotGrowUnbounded()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            // 喂 100 帧，间隔 100ms → 覆盖 10s，超出 RetentionMs
            for (int i = 0; i < 100; i++)
            {
                await channel.WriteAsync(MakeFrame(Id));
                clock.Ticks += 100;
            }
            // now ≈ 1_010_000；查询自 1_008_000 起 → 仅最近 ~20 帧，更旧的被淘汰
            collector.CountSince(Id, 1_008_000, 1_010_000).Should().Be(20);
        }
        finally { channel.Dispose(); }
    }

    // ---- AssertNoFrame ----

    [Fact]
    public async Task AssertNoFrame_NoFrames_Passes()
    {
        var (channel, collector, _) = await SetupAsync();
        try
        {
            var executor = new AssertNoFrameStepExecutor(collector);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new AssertNoFrameStep(Id, "50")), null!, default);

            result.Status.Should().Be(StepStatus.Passed);
        }
        finally { channel.Dispose(); }
    }

    [Fact]
    public async Task AssertNoFrame_FrameArrives_Fails()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            var executor = new AssertNoFrameStepExecutor(collector);
            var task = executor.ExecuteAsync(
                TestCaseStep.Create(new AssertNoFrameStep(Id, "200")), null!, default);

            await Task.Delay(10);                       // 确保 since 已打点
            clock.Ticks += 50;                          // 窗口内出现帧
            await channel.WriteAsync(MakeFrame(Id));    // t = 1_000_050

            var result = await task;
            result.Status.Should().Be(StepStatus.Failed);
        }
        finally { channel.Dispose(); }
    }

    // ---- AssertFrameCount ----

    [Fact]
    public async Task AssertFrameCount_CountInRange_Passes()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            var executor = new AssertFrameCountStepExecutor(collector);
            var task = executor.ExecuteAsync(
                TestCaseStep.Create(new AssertFrameCountStep(Id, "200", "2", "5")), null!, default);

            await Task.Delay(10);
            for (int i = 0; i < 3; i++)
            {
                clock.Ticks += 50;
                await channel.WriteAsync(MakeFrame(Id));
            }

            var result = await task;
            result.Status.Should().Be(StepStatus.Passed);
        }
        finally { channel.Dispose(); }
    }

    // ---- AssertCycleTime ----

    [Fact]
    public async Task AssertCycleTime_PeriodStable_Passes()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            var executor = new AssertCycleTimeStepExecutor(collector);
            var task = executor.ExecuteAsync(
                TestCaseStep.Create(new AssertCycleTimeStep(Id, "500", "80", "120", "3")), null!, default);

            await Task.Delay(10);
            // 窗口 [1_000_000, 1_000_500] 内 5 帧，间隔 100ms
            for (int i = 0; i < 5; i++)
            {
                clock.Ticks += 100;
                await channel.WriteAsync(MakeFrame(Id));
            }

            var result = await task;
            result.Status.Should().Be(StepStatus.Passed);
        }
        finally { channel.Dispose(); }
    }

    [Fact]
    public async Task AssertCycleTime_PeriodUnstable_Fails()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            var executor = new AssertCycleTimeStepExecutor(collector);
            var task = executor.ExecuteAsync(
                TestCaseStep.Create(new AssertCycleTimeStep(Id, "700", "80", "120", "3")), null!, default);

            await Task.Delay(10);
            // 间隔 100,100,300,100 → Mean=150 > MaxMs=120
            int[] offsets = { 100, 100, 300, 100 };
            foreach (var offset in offsets)
            {
                clock.Ticks += offset;
                await channel.WriteAsync(MakeFrame(Id));
            }

            var result = await task;
            result.Status.Should().Be(StepStatus.Failed);
        }
        finally { channel.Dispose(); }
    }

    [Fact]
    public async Task AssertCycleTime_InsufficientSamples_Fails()
    {
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            var executor = new AssertCycleTimeStepExecutor(collector);
            var task = executor.ExecuteAsync(
                TestCaseStep.Create(new AssertCycleTimeStep(Id, "300", "80", "120", "3")), null!, default);

            await Task.Delay(10);
            // 窗口 [1_000_000, 1_000_300] 内仅 2 帧 < MinSamples=3
            for (int i = 0; i < 2; i++)
            {
                clock.Ticks += 100;
                await channel.WriteAsync(MakeFrame(Id));
            }

            var result = await task;
            result.Status.Should().Be(StepStatus.Failed);
            result.Message.Should().Contain("need >= 3");
        }
        finally { channel.Dispose(); }
    }

    // ---- review 回归 ----

    [Fact]
    public async Task Collector_LongWindow_NotTruncatedByRetention()
    {
        // review H1: WindowMs > RetentionMs(5s) 时窗口内帧必须保留，不得被懒淘汰截断
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            await channel.WriteAsync(MakeFrame(Id));          // t = 1_000_000
            clock.Ticks += 1000;
            await channel.WriteAsync(MakeFrame(Id));          // 1_001_000
            clock.Ticks += 7000;                              // 查询时刻 1_008_000（窗口 8s > 5s）

            collector.CountSince(Id, 1_000_000, 1_008_000).Should().Be(2);
        }
        finally { channel.Dispose(); }
    }

    [Fact]
    public async Task AssertCycleTime_OutlierInterval_FailsDespiteMeanInRange()
    {
        // review M1: 间隔 50,50,250 → Mean=116.7 在 [80,120] 内，但 250ms 越界必须 Failed（逐区间判定）
        var (channel, collector, clock) = await SetupAsync();
        try
        {
            var executor = new AssertCycleTimeStepExecutor(collector);
            var task = executor.ExecuteAsync(
                TestCaseStep.Create(new AssertCycleTimeStep(Id, "700", "80", "120", "3")), null!, default);

            await Task.Delay(10);
            int[] offsets = { 50, 50, 250 };
            foreach (var offset in offsets)
            {
                clock.Ticks += offset;
                await channel.WriteAsync(MakeFrame(Id));
            }

            var result = await task;
            result.Status.Should().Be(StepStatus.Failed);   // MaxMs=250 > 120
        }
        finally { channel.Dispose(); }
    }
}
