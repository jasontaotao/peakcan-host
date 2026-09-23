using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Tests.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.J1939;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// <see cref="J1939TpSessionEventSink"/> 缓冲 + ~30Hz 滴答批量投递（发散审查 HIGH 修复）：
/// 读线程只缓冲，FakeTimerFactory 的 <see cref="FakeCyclicTimer.Fire"/> 确定性驱动单次滴答，
/// 一次性灌入 <see cref="TraceViewModel.J1939SessionEvents"/>。
/// 帧构造辅助（<c>Frame</c>/<c>BamSequence</c>）复刻 <c>J1939TpLayerWatchdogTests</c>。
/// </summary>
public class J1939TpSessionEventSinkTests
{
    /// <summary>
    /// IsEnabled=true 的捕获 logger：让 [LoggerMessage] 模板真正求值并断言**每批一条摘要**
    /// （NullLogger 下 IsEnabled=false 直接短路，模板/节流行为测不出来）。
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }

    private static CanFrame Frame(uint rawId, byte[] data) =>
        new(new CanId(rawId, FrameFormat.Extended), data, FrameFlags.None, ChannelId.None, new Timestamp(1_000_000));

    private static List<CanFrame> BamSequence(byte[] payload, uint pgn, byte sa)
    {
        var frames = new List<CanFrame>();
        var cmId = J1939Id.Compose(6, 0x00EC00, sa, 0xFF);
        var dtId = J1939Id.Compose(6, 0x00EB00, sa, 0xFF);
        frames.Add(Frame(cmId, TpCmMessage.Bam((ushort)payload.Length, (byte)((payload.Length + 6) / 7), pgn).Encode()));
        for (int i = 0; i < (payload.Length + 6) / 7; i++)
        {
            int take = Math.Min(7, payload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(payload, i * 7, chunk, 0, take);
            frames.Add(Frame(dtId, new TpDtMessage((byte)(i + 1), chunk).Encode()));
        }
        return frames;
    }

    /// <summary>
    /// 构造 sink + StartAsync 订阅并注册 FakeTimerFactory 滴答。测试环境无
    /// <c>Application.Current</c>，Fire 触发 <c>RunOnUiPost</c> → inline，断言同步可见。
    /// </summary>
    private static (J1939TpSessionEventSink Sink, FakeTimerFactory Timer) CreateStartedSink(
        J1939TpLayer layer, TraceViewModel vm, ILogger<J1939TpSessionEventSink>? logger = null)
    {
        var timer = new FakeTimerFactory();
        var sink = new J1939TpSessionEventSink(
            layer, vm, logger ?? NullLogger<J1939TpSessionEventSink>.Instance, timer);
        sink.StartAsync(default).GetAwaiter().GetResult();
        return (sink, timer);
    }

    [Fact]
    public async Task Online_Bam_Gap_Forwards_PacketLoss_On_Drain()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            new J1939TpOptions(), null, clock);
        var vm = new TraceViewModel();
        var (sink, timer) = CreateStartedSink(layer, vm);
        try
        {
            var seq = BamSequence(Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray(), 0x000200, 0xF4);
            foreach (var f in seq.Take(3)) layer.ProcessFrame(f);   // CM + DT#1..2
            layer.ProcessFrame(seq[4]);                              // DT#4（跳过 #3 → 在线缺口）

            vm.J1939SessionEvents.Should().BeEmpty();                // 缓冲期未投递
            timer.CreatedCyclicTimers.Single().Fire();

            var row = vm.J1939SessionEvents.Should().ContainSingle().Subject;
            row.Kind.Should().Be(SessionEventKind.PacketLoss);
            row.Sa.Should().Be(0xF4);
            row.Da.Should().Be(0xFF);
            row.Pgn.Should().Be(0x000200);
            row.Detail.Should().Contain("expected seq");
            row.Channel.Should().Be(ChannelId.None);                 // 测试帧 ChannelId.None
            row.TimestampSec.Should().Be(1.0);                       // Frame helper 恒 1e6 us
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    [Fact]
    public async Task Offline_Repeated_Bam_Forwards_Superseded_On_Drain()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            J1939TpOptions.Offline, null, clock);
        var vm = new TraceViewModel();
        var (sink, timer) = CreateStartedSink(layer, vm);
        try
        {
            // 同 (Sa=0xF4, Da=0xFF) 连续两次 BAM CM：第二次 supersede 第一次（restarted）
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);
            layer.ProcessFrame(BamSequence(new byte[7], 0x000300, 0xF4)[0]);

            timer.CreatedCyclicTimers.Single().Fire();

            vm.J1939SessionEvents.Should().ContainSingle(e => e.Kind == SessionEventKind.Superseded);
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    [Fact]
    public async Task Offline_Session_Table_Full_Forwards_Evicted_On_Drain()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            new J1939TpOptions { MaxConcurrentSessions = 2 }, null, clock);
        var vm = new TraceViewModel();
        var (sink, timer) = CreateStartedSink(layer, vm);
        try
        {
            // 3 个不同 SA 的 BAM CM（各 1 包）→ 表满（上限 2）→ 第 3 个驱逐最旧
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0x11)[0]);
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0x22)[0]);
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0x33)[0]);

            timer.CreatedCyclicTimers.Single().Fire();

            vm.J1939SessionEvents.Should().ContainSingle(e => e.Kind == SessionEventKind.Evicted);
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    /// <summary>
    /// 洪峰（发散审查 HIGH）：1500 次重复 CM → 1499 个 Superseded。缓冲期集合为空；
    /// 单次 Fire 一次性灌入并封顶到 <see cref="TraceViewModel.J1939SessionEventsCap"/>——
    /// 证明不再逐事件 RunOnUiPost/逐事件裁剪。
    /// </summary>
    [Fact]
    public async Task Flood_Is_Buffered_Until_Drain_Then_Capped()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            J1939TpOptions.Offline, null, clock);
        var vm = new TraceViewModel();
        var (sink, timer) = CreateStartedSink(layer, vm);
        try
        {
            for (int i = 0; i < 1500; i++)
                layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);   // 同 (Sa,Da) 反复 CM → Superseded 洪峰

            vm.J1939SessionEvents.Should().BeEmpty();                              // 缓冲期零投递
            timer.CreatedCyclicTimers.Single().Fire();

            vm.J1939SessionEvents.Should().HaveCount(TraceViewModel.J1939SessionEventsCap);
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    /// <summary>
    /// 真 logger 下模板可求值 + 日志节流（LOW-1）：洪峰 1500 事件单次滴答只落**一条**
    /// 9303 摘要，非逐事件 1500 条。
    /// </summary>
    [Fact]
    public async Task Real_Logger_Logs_One_Summary_Per_Drain()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            J1939TpOptions.Offline, null, clock);
        var vm = new TraceViewModel();
        var logger = new CapturingLogger<J1939TpSessionEventSink>();
        var (sink, timer) = CreateStartedSink(layer, vm, logger);
        try
        {
            for (int i = 0; i < 1500; i++)
                layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);

            timer.CreatedCyclicTimers.Single().Fire();

            logger.Messages.Should().ContainSingle().Which.Should().Contain("J1939 session events");
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    /// <summary>
    /// 二次滴答只投递新一批（review LOW-3 硬化）：首次滴答后再次滴答不重复回放旧批次；
    /// 无新事件时滴答为空操作。
    /// </summary>
    [Fact]
    public async Task Second_Drain_Delivers_Fresh_Batch_Only()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            J1939TpOptions.Offline, null, clock);
        var vm = new TraceViewModel();
        var (sink, timer) = CreateStartedSink(layer, vm);
        try
        {
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);   // 建会话
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);   // 事件 A（Superseded）
            timer.CreatedCyclicTimers.Single().Fire();
            vm.J1939SessionEvents.Should().ContainSingle();

            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);   // 事件 B（新一批）
            timer.CreatedCyclicTimers.Single().Fire();
            vm.J1939SessionEvents.Should().HaveCount(2);                       // A + B，无重复

            timer.CreatedCyclicTimers.Single().Fire();                         // 空批次滴答 → no-op
            vm.J1939SessionEvents.Should().HaveCount(2);
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    /// <summary>
    /// 重启生命周期（review MEDIUM 钉住）：无 StopAsync 的二次 StartAsync 必须停旧滴答
    /// 定时器——否则旧表仍可 Fire → 与真实滴答重复投递。Fire 顺序：真实表（index 1）先消费 A，
    /// 旧表（index 0）随后对 B 投递——修复后旧表已 dispose → no-op，集合恰 1 行；旧实现 2 行。
    /// </summary>
    [Fact]
    public async Task Restart_Disposes_Previous_Timer()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            J1939TpOptions.Offline, null, clock);
        var vm = new TraceViewModel();
        var (sink, timer) = CreateStartedSink(layer, vm);
        await sink.StartAsync(default);              // 二次 StartAsync（无 StopAsync）→ 应停 timer[0]
        try
        {
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);   // 建会话
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);   // 事件 A
            timer.CreatedCyclicTimers[1].Fire();     // 真实滴答 → A 入列
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0xF4)[0]);   // 事件 B
            timer.CreatedCyclicTimers[0].Fire();     // 旧表：dispose → no-op（旧实现会 +B）

            vm.J1939SessionEvents.Should().HaveCount(1);   // 修复后恰 1；旧实现 2（重复投递）
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }
}
