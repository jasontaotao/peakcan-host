using System.Collections.ObjectModel;
using System.Diagnostics;
using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// Unit tests for <see cref="IsoTpLayer"/> covering the 3 CRITICAL bugs found
/// in the UDS audit (2026-06-24):
/// <list type="bullet">
/// <item><b>C-4</b>: Flow Control Block Size was discarded — tester sent all CFs in one burst.</item>
/// <item><b>C-5</b>: STmin unit was always treated as milliseconds (0xF1=241 instead of 100 µs).</item>
/// <item><b>C-6</b>: No N_Cr / N_Bs timeout — half-finished reassemblies or missing FCs caused permanent hangs.</item>
/// </list>
/// <para>
/// <see cref="IsoTpLayer"/> is sealed, so tests use the production class directly:
/// the outgoing CAN sink is the constructor's <c>Action&lt;CanFrame&gt;</c>;
/// the inbound hook is the public <see cref="IsoTpLayer.MessageReceived"/> event
/// raised after <see cref="IsoTpLayer.ProcessFrame"/> reassembles a complete
/// message.
/// </para>
/// </summary>
public sealed class IsoTpLayerTests
{
    private const uint ReqId = 0x7E0;
    private const uint RespId = 0x7E8;

    private static CanIdConfig DefaultConfig
        => new() { RequestId = ReqId, ResponseId = RespId };

    /// <summary>Builds an IsoTpLayer whose outbound frames are appended to <paramref name="sentSink"/>.</summary>
    private static IsoTpLayer NewLayer(ObservableCollection<byte[]> sentSink, CanIdConfig? config = null)
        => new(config ?? DefaultConfig, frame => sentSink.Add(frame.Data.ToArray()));

    private static void InjectRawFrame(IsoTpLayer iso, byte[] canData)
        => iso.ProcessFrame(new CanFrame(
            new CanId(RespId, FrameFormat.Standard),
            canData, FrameFlags.None, default, default));

    private static void InjectFlowControl(IsoTpLayer iso, byte flowStatus = 0, byte blockSize = 0, byte stMin = 0)
        => InjectRawFrame(iso, new IsoTpFrame(
            IsoTpFrameType.FlowControl,
            sequenceOrStatus: flowStatus,
            blockSize: blockSize,
            stMin: stMin).Encode());

    private static void InjectFirstFrame(IsoTpLayer iso, int totalLength, byte[] firstChunk)
        => InjectRawFrame(iso, new IsoTpFrame(
            IsoTpFrameType.First, length: totalLength, data: firstChunk).Encode());

    private static void InjectConsecutiveFrame(IsoTpLayer iso, byte sequence, byte[] chunk)
        => InjectRawFrame(iso, new IsoTpFrame(
            IsoTpFrameType.Consecutive, sequenceOrStatus: sequence, data: chunk).Encode());

    private static int ConsecutiveFrameCount(ObservableCollection<byte[]> sent)
        => sent.Count(f => (f[0] & 0xF0) == 0x20);

    /// <summary>Wait until the outbound count reaches <paramref name="target"/> or timeout.</summary>
    private static async Task WaitForSentFramesAsync(ObservableCollection<byte[]> sink, int target, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (sink.Count < target && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }

    // ========================================================================
    // C-4: Block Size must gate the CF burst per ISO 15765-2 §6.5.5.
    // ========================================================================

    [Fact]
    public async Task SendMultiFrameAsync_WithBlockSize_2_Waits_After_Second_CF()
    {
        var sink = new ObservableCollection<byte[]>();
        var iso = NewLayer(sink);

        // 30-byte payload: FF(6) + CF1(7) + CF2(7) + CF3(7) + CF4(3) = 30. 4 CFs total.
        var payload = new byte[30];
        var sendTask = iso.SendMessageAsync(payload, CancellationToken.None);

        await WaitForSentFramesAsync(sink, 1); // wait for FF

        // Inject FC(BS=2, STmin=0). With the BS gate the tester must pause
        // after CF2 until the next FC arrives.
        InjectFlowControl(iso, blockSize: 2, stMin: 0);

        await WaitForSentFramesAsync(sink, 3); // FF + 2 CFs
        ConsecutiveFrameCount(sink).Should().Be(2);

        // Give the layer a moment. Without the BS gate, CF3/CF4 would already
        // be queued. With the gate, the tester must wait for the next FC.
        await Task.Delay(150);
        sendTask.IsCompleted.Should().BeFalse(
            "tester must wait for the next Flow Control after BS CFs (ISO 15765-2 §6.5.5)");

        // Inject another FC(BS=0) to allow completion.
        InjectFlowControl(iso, blockSize: 0, stMin: 0);
        await sendTask.WaitAsync(TimeSpan.FromSeconds(2));

        sink.Should().HaveCount(5); // 1 FF + 4 CFs
        ConsecutiveFrameCount(sink).Should().Be(4);
    }

    [Fact]
    public async Task SendMultiFrameAsync_BlockSizeZero_Sends_All_CFs_Without_Waiting()
    {
        var sink = new ObservableCollection<byte[]>();
        var iso = NewLayer(sink);

        var payload = new byte[30]; // 1 FF + 4 CFs
        var sendTask = iso.SendMessageAsync(payload, CancellationToken.None);

        await WaitForSentFramesAsync(sink, 1);
        InjectFlowControl(iso, blockSize: 0, stMin: 0); // BS=0 → unlimited

        await sendTask.WaitAsync(TimeSpan.FromSeconds(2));

        sink.Should().HaveCount(5);
        ConsecutiveFrameCount(sink).Should().Be(4);
    }

    // ========================================================================
    // C-5: STmin must respect the ISO 15765-2 §6.5.5.4 unit split:
    //      0x00..0x7F → 0..127 ms
    //      0xF1..0xF9 → 100..900 µs
    // ========================================================================

    [Fact]
    public async Task HandleFlowControl_StMin_0x05_Applies_5ms_Per_CF_Delay()
    {
        var sink = new ObservableCollection<byte[]>();
        var iso = NewLayer(sink);

        // 27 bytes → FF(6) + CF1(7) + CF2(7) + CF3(7) = 27. 2 inter-CF delays.
        var payload = new byte[27];
        var sw = Stopwatch.StartNew();
        var sendTask = iso.SendMessageAsync(payload, CancellationToken.None);

        await WaitForSentFramesAsync(sink, 1); // FF
        InjectFlowControl(iso, blockSize: 0, stMin: 5); // 5 ms per CF gap

        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        // 2 delays × 5 ms = ~10 ms expected.
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(8));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task HandleFlowControl_StMin_0xF1_Applies_100us_Not_241ms_Per_CF_Delay()
    {
        var sink = new ObservableCollection<byte[]>();
        var iso = NewLayer(sink);

        var payload = new byte[27]; // 2 inter-CF delays
        var sw = Stopwatch.StartNew();
        var sendTask = iso.SendMessageAsync(payload, CancellationToken.None);

        await WaitForSentFramesAsync(sink, 1); // FF
        InjectFlowControl(iso, blockSize: 0, stMin: 0xF1); // 100 µs per CF gap

        await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        // With the fix: 2 delays × 100 µs = ~200 µs. Without the fix the
        // layer treats 0xF1 as 241 ms, totalling ~482 ms — fails loudly.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(50),
            "STmin=0xF1 is 100 µs per ISO 15765-2 §6.5.5.4, not 241 ms");
    }

    // ========================================================================
    // C-6: N_Cr (receive CF timeout) and N_Bs (send FC-wait timeout) must
    //      reset state / raise TimeoutException respectively.
    // ========================================================================

    [Fact]
    public async Task HandleConsecutiveFrame_Timeout_After_NCr_Resets_Reassembly()
    {
        var sink = new ObservableCollection<byte[]>();
        var iso = NewLayer(sink);

        // Configure N_Cr=150ms via the new property.
        iso.ReceiveTimeout = TimeSpan.FromMilliseconds(150);

        // Round 1: inject FF (length=20) but never send CFs → reassembly must
        // time out and clear _rxInProgress.
        InjectFirstFrame(iso, totalLength: 20, firstChunk: new byte[] { 1, 2, 3, 4, 5, 6 });

        // Round 2: subscribe and inject a fresh FF + CF for a 10-byte message.
        byte[]? receivedPayload = null;
        var done = new TaskCompletionSource();
        iso.MessageReceived += bytes =>
        {
            receivedPayload = bytes;
            done.TrySetResult();
        };

        // Wait for the first reassembly to time out (> N_Cr).
        await Task.Delay(250);

        // Fresh FF(10) + CF1(4) — MessageReceived must fire with the 10-byte payload.
        InjectFirstFrame(iso, totalLength: 10, firstChunk: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF });
        InjectConsecutiveFrame(iso, sequence: 1, chunk: new byte[] { 0x11, 0x22, 0x33, 0x44 });

        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        completed.Should().Be(done.Task,
            "after N_Cr timeout the layer must accept a new FF and reassemble normally");
        receivedPayload.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44);
    }

    [Fact]
    public async Task SendMultiFrameAsync_NoFlowControl_Throws_Timeout_Before_Default_5s()
    {
        var sink = new ObservableCollection<byte[]>();
        var iso = NewLayer(sink);

        // Configure N_Bs=200ms via the new property.
        iso.FlowControlTimeout = TimeSpan.FromMilliseconds(200);

        var payload = new byte[30];
        var sw = Stopwatch.StartNew();

        Func<Task> act = async () =>
            await iso.SendMessageAsync(payload, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "with N_Bs=200ms the layer must give up well before the hardcoded 5s");
    }
}