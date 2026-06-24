using System.Collections.ObjectModel;
using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// Unit tests for <see cref="UdsClient"/> covering 3 CRITICAL bugs found in
/// the UDS audit (2026-06-24):
/// <list type="bullet">
/// <item><b>C-3</b>: ECU-negotiated P2/P2* were never written to <see cref="UdsTimer"/>.</item>
/// <item><b>C-7</b>: <c>RequestDownloadAsync</c> accessed <c>response[5]</c> after a <c>&lt; 3</c> length check.</item>
/// <item><b>C-8</b>: <c>OnMessageReceived</c> did not validate positive response SID+0x40 against the request.</item>
/// </list>
/// <para>
/// <see cref="IsoTpLayer"/> is sealed, so we drive it directly: outbound frames
/// land in the constructor's <c>Action&lt;CanFrame&gt;</c> sink, and inbound
/// frames are injected via the public <see cref="IsoTpLayer.ProcessFrame"/>
/// method (which raises <see cref="IsoTpLayer.MessageReceived"/> after
/// reassembly). For single-frame UDS responses (≤ 7 bytes), a single
/// <c>ProcessFrame</c> call delivers the message.
/// </para>
/// </summary>
public sealed class UdsClientTests
{
    private const uint ReqId = 0x7E0;
    private const uint RespId = 0x7E8;

    private static (IsoTpLayer iso, ObservableCollection<byte[]> sent) NewIso()
    {
        var sent = new ObservableCollection<byte[]>();
        var iso = new IsoTpLayer(
            new CanIdConfig { RequestId = ReqId, ResponseId = RespId },
            frame => sent.Add(frame.Data.ToArray()));
        return (iso, sent);
    }

    /// <summary>
    /// Inject a complete UDS message via ISO-TP Single Frame reassembly.
    /// For payloads ≤ 7 bytes this is a one-frame round trip.
    /// </summary>
    private static void EcuRespond(IsoTpLayer iso, byte[] payload)
    {
        var isoFrame = new IsoTpFrame(IsoTpFrameType.Single, data: payload);
        var canData = isoFrame.Encode();
        iso.ProcessFrame(new CanFrame(
            new CanId(RespId, FrameFormat.Standard),
            canData, FrameFlags.None, default, default));
    }

    /// <summary>Inject a Flow Control frame (BS=0 unlimited, STmin=0).</summary>
    private static void InjectFlowControl(IsoTpLayer iso)
    {
        var fc = new IsoTpFrame(
            IsoTpFrameType.FlowControl,
            sequenceOrStatus: 0, blockSize: 0, stMin: 0);
        iso.ProcessFrame(new CanFrame(
            new CanId(RespId, FrameFormat.Standard),
            fc.Encode(), FrameFlags.None, default, default));
    }

    /// <summary>
    /// Drain any First Frame from the outbound sink and reply with a Flow
    /// Control. Tests use this to advance multi-frame requests past the FF.
    /// </summary>
    private static async Task PumpFirstFrameAndFc(IsoTpLayer iso, ObservableCollection<byte[]> sent)
    {
        // Wait for FF (PCI high nibble = 0x1).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var f in sent)
            {
                if ((f[0] & 0xF0) == 0x10)
                {
                    InjectFlowControl(iso);
                    return;
                }
            }
            await Task.Delay(5);
        }
        throw new TimeoutException("No First Frame observed on the sink");
    }

    // ========================================================================
    // C-3: ECU-negotiated P2 / P2* must be propagated to UdsTimer.
    // ========================================================================

    [Fact]
    public async Task DiagnosticSessionControlAsync_WritesNegotiatedP2_Into_Timer()
    {
        var (iso, _) = NewIso();
        var timer = new UdsTimer();
        using var client = new UdsClient(iso, timer);

        // Trigger request, then simulate ECU reply with P2=0x0190 (400 ms),
        // P2*=0x01F4 (500 ms). The fix must write these to UdsTimer.
        var task = client.DiagnosticSessionControlAsync(0x02);
        EcuRespond(iso, new byte[] { 0x50, 0x02, 0x01, 0x90, 0x01, 0xF4 });

        var resp = await task.WaitAsync(TimeSpan.FromSeconds(1));

        resp.SessionType.Should().Be(0x02);
        timer.P2Timeout.Should().Be(TimeSpan.FromMilliseconds(0x0190));
        timer.P2StarTimeout.Should().Be(TimeSpan.FromMilliseconds(0x01F4));
    }

    [Fact]
    public async Task DiagnosticSessionControlAsync_NegativeResponse_DoesNotMutate_Timer()
    {
        var (iso, _) = NewIso();
        var timer = new UdsTimer();
        var originalP2 = timer.P2Timeout;
        var originalP2Star = timer.P2StarTimeout;
        using var client = new UdsClient(iso, timer);

        // ECU rejects with NRC 0x12 (subFunctionNotSupported).
        var task = client.DiagnosticSessionControlAsync(0x02);
        EcuRespond(iso, new byte[] { 0x7F, 0x10, 0x12 });

        Func<Task> act = async () => await task;
        await act.Should().ThrowAsync<UdsNegativeResponseException>();

        // Timer must NOT be updated when the request is rejected.
        timer.P2Timeout.Should().Be(originalP2);
        timer.P2StarTimeout.Should().Be(originalP2Star);
    }

    // ========================================================================
    // C-7: RequestDownload must validate response length before reading bytes.
    // ========================================================================

    [Fact]
    public async Task RequestDownloadAsync_Throws_UdsException_On_TruncatedResponse()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.RequestDownloadAsync(0x1000, 0x100);

        // RequestDownload sends an 11-byte payload → multi-frame; pump FF → FC.
        await PumpFirstFrameAndFc(iso, sent);

        // Truncated response: 4 bytes total → UdsClient sees 3 bytes after stripping SID.
        // The current < 3 length check passes (3 >= 3) but response[5] is OOB,
        // producing IndexOutOfRangeException instead of UdsException.
        EcuRespond(iso, new byte[] { 0x74, 0x20, 0x00, 0x01 });

        Func<Task> act = async () => await task;
        var thrown = await act.Should().ThrowAsync<UdsException>();
        thrown.WithMessage("*RequestDownload*");
    }

    [Fact]
    public async Task RequestDownloadAsync_Accepts_Full_Six_Byte_Response()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.RequestDownloadAsync(0x1000, 0x100);

        // Pump FF → FC for the multi-frame request.
        await PumpFirstFrameAndFc(iso, sent);

        // Full response: [dataFormatId, lengthFormatId, maxLength x 4]
        // maxLength = 0x00001000
        EcuRespond(iso, new byte[] { 0x74, 0x20, 0x00, 0x00, 0x10, 0x00 });

        var blockLen = await task.WaitAsync(TimeSpan.FromSeconds(2));
        blockLen.Should().Be(0x00001000);
    }

    // ========================================================================
    // C-8: OnMessageReceived must validate positive response SID == request SID + 0x40.
    // ========================================================================

    [Fact]
    public async Task SendRequestAsync_DropsPositiveResponse_WithMismatched_SID()
    {
        var (iso, _) = NewIso();
        // Use a long P2 so the request hangs (proving the wrong SID was dropped,
        // not just timing out via a normal positive response).
        var timer = new UdsTimer { P2Timeout = TimeSpan.FromMilliseconds(500) };
        using var client = new UdsClient(iso, timer);

        // Request SID 0x22 (ReadDataByIdentifier). Inject an SID of 0x71 — wrong
        // both in bit-7 and as a SID echo. The fix must drop this frame silently.
        var task = client.SendRequestAsync(0x22, new byte[] { 0xF1, 0x90 });
        EcuRespond(iso, new byte[] { 0x71, 0xF1, 0x90, 0x41 });

        // Give the layer a moment — without the fix, the task would complete
        // with the bogus payload. With the fix, it must stay pending.
        await Task.Delay(100);
        task.IsCompleted.Should().BeFalse(
            "an SID that doesn't echo the request's SID+0x40 must be discarded (ISO 14229 §11.2.2.2)");

        // Now inject the CORRECT SID (0x22 + 0x40 = 0x62) and verify the request completes.
        EcuRespond(iso, new byte[] { 0x62, 0xF1, 0x90, 0x41 });
        var resp = await task.WaitAsync(TimeSpan.FromSeconds(2));
        resp.Should().Equal(0xF1, 0x90, 0x41);
    }

    [Fact]
    public async Task SendRequestAsync_AcceptsPositiveResponse_With_SID_Plus_0x40()
    {
        var (iso, _) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.SendRequestAsync(0x22, new byte[] { 0xF1, 0x90 });
        EcuRespond(iso, new byte[] { 0x62, 0xF1, 0x90, 0x41 });

        var resp = await task.WaitAsync(TimeSpan.FromSeconds(1));
        resp.Should().Equal(0xF1, 0x90, 0x41);
    }
}