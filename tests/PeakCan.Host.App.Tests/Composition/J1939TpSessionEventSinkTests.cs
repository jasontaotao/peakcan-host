using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.J1939;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// <see cref="J1939TpSessionEventSink"/> 订阅在线 <see cref="J1939TpLayer.SessionEvent"/>
/// （SDK 读线程同步引发）并把会话异常 marshal 进
/// <see cref="TraceViewModel.J1939SessionEvents"/>。帧构造辅助（<c>Frame</c>/<c>BamSequence</c>）
/// 复刻 <c>J1939TpLayerWatchdogTests</c> 的对应实现。
/// </summary>
public class J1939TpSessionEventSinkTests
{
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

    [Fact]
    public async Task Online_Bam_Gap_Forwards_PacketLoss_To_Vm_Collection()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            new J1939TpOptions(), null, clock);
        var vm = new TraceViewModel();
        var sink = new J1939TpSessionEventSink(layer, vm, NullLogger<J1939TpSessionEventSink>.Instance);
        await sink.StartAsync(default);
        try
        {
            var seq = BamSequence(Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray(), 0x000200, 0xF4);
            foreach (var f in seq.Take(3)) layer.ProcessFrame(f);   // CM + DT#1..2
            layer.ProcessFrame(seq[4]);                              // DT#4（跳过 #3 → 在线缺口）

            vm.J1939SessionEvents.Should().ContainSingle();
            var row = vm.J1939SessionEvents[0];
            row.Kind.Should().Be(SessionEventKind.PacketLoss);
            row.Sa.Should().Be(0xF4);
            row.Da.Should().Be(0xFF);
            row.Pgn.Should().Be(0x000200);
            row.Detail.Should().Contain("expected seq");
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    [Fact]
    public async Task Offline_Session_Table_Full_Forwards_Evicted_To_Vm_Collection()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            new J1939TpOptions { MaxConcurrentSessions = 2 }, null, clock);
        var vm = new TraceViewModel();
        var sink = new J1939TpSessionEventSink(layer, vm, NullLogger<J1939TpSessionEventSink>.Instance);
        await sink.StartAsync(default);
        try
        {
            // 3 个不同 SA 的 BAM CM（各 1 包）→ 表满（上限 2）→ 第 3 个驱逐最旧
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0x11)[0]);
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0x22)[0]);
            layer.ProcessFrame(BamSequence(new byte[7], 0x000200, 0x33)[0]);

            vm.J1939SessionEvents.Should().ContainSingle(e => e.Kind == SessionEventKind.Evicted);
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }

    [Fact]
    public async Task Offline_Repeated_Bam_Forwards_Superseded_To_Vm_Collection()
    {
        var clock = new FakeTimeProvider();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            J1939TpOptions.Offline, null, clock);
        var vm = new TraceViewModel();
        var sink = new J1939TpSessionEventSink(layer, vm, NullLogger<J1939TpSessionEventSink>.Instance);
        await sink.StartAsync(default);
        try
        {
            // 同 (Sa=0xF4, Da=0xFF) 连续两次 BAM CM：第二次 supersede 第一次（restarted）
            var seq1 = BamSequence(Enumerable.Range(0, 15).Select(i => (byte)(i + 1)).ToArray(), 0x000200, 0xF4);
            var seq2 = BamSequence(Enumerable.Range(0, 15).Select(i => (byte)(i + 1)).ToArray(), 0x000300, 0xF4);
            layer.ProcessFrame(seq1[0]);   // CM #1（PGN 0x200）
            layer.ProcessFrame(seq2[0]);   // CM #2 同 SA → supersede

            vm.J1939SessionEvents.Should().ContainSingle(e => e.Kind == SessionEventKind.Superseded);
        }
        finally
        {
            await sink.StopAsync(default);
        }
    }
}
