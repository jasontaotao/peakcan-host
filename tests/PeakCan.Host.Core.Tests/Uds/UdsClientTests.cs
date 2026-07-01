using System.Collections.ObjectModel;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    // ========================================================================
    // v1.2.13 PATCH Item 4: P2 timeout must unblock await _responseTcs.Task
    // and dispose-guard must tolerate late-arriving responses.
    // ========================================================================

    /// <summary>
    /// Build a UdsClient backed by a real IsoTpLayer whose outbound sink
    /// discards frames (no real CAN bus) and whose MessageReceived is
    /// never raised by external traffic. Returns the client plus a
    /// <see cref="MockTransport"/> wrapper so tests can drive
    /// <see cref="UdsClient.PublicOnMessageReceivedForTesting"/> directly.
    /// </summary>
    private static (UdsClient client, MockTransport transport) MakeClientWithMockedTransport()
    {
        var sent = new ObservableCollection<byte[]>();
        var iso = new IsoTpLayer(
            new CanIdConfig { RequestId = ReqId, ResponseId = RespId },
            frame => sent.Add(frame.Data.ToArray()));
        var timer = new UdsTimer
        {
            P2Timeout = TimeSpan.FromMilliseconds(50),
            P2StarTimeout = TimeSpan.FromMilliseconds(5000),
        };
        var client = new UdsClient(iso, timer);
        return (client, new MockTransport(iso));
    }

    /// <summary>
    /// v1.2.13 PATCH Item 4: P2 timeout must unblock await _responseTcs.Task
    /// (currently only cancels the linked CTS token, which nothing awaits).
    /// Without this fix, the caller hangs forever waiting for a response
    /// that will never arrive. The fix registers a Token.Register callback
    /// that calls _responseTcs.TrySetCanceled on P2 timeout.
    /// </summary>
    [Fact]
    public async Task SendRequestAsync_P2Timeout_TriesSetCanceled_ResponseTcs()
    {
        var (client, _) = MakeClientWithMockedTransport();
        // Drive a request but never send a response.
        var p2FiredTcs = new TaskCompletionSource();
        client.OnP2TimeoutFiredForTesting = () => p2FiredTcs.TrySetResult();

        Func<Task> act = () => client.SendRequestAsync(0x22, [0x00, 0x01]);
        var stopwatch = Stopwatch.StartNew();
        await act.Should().ThrowAsync<UdsException>(
            "P2 timeout must surface as UdsException, not hang forever");
        stopwatch.Stop();

        // The configured P2 in MakeClientWithMockedTransport is 50 ms.
        // Budget = 500 ms (~10× P2) so a hang in the fix surfaces fast but
        // CI jitter on a loaded agent doesn't false-positive the test.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "P2 timeout must fire within ~10× the configured P2 (50 ms), not hang for seconds");

        (await Task.WhenAny(p2FiredTcs.Task, Task.Delay(500)))
            .Should().Be(p2FiredTcs.Task,
                "OnP2TimeoutFiredForTesting hook must fire on auto-timeout");
    }

    /// <summary>
    /// v1.2.13 PATCH Item 4 (Phase 2.5 new finding): a late-arriving
    /// response on the SDK read thread must NOT throw ObjectDisposedException
    /// when the in-flight CTS has been Dispose'd by SendRequestInternalAsync's
    /// finally block. The cts?.CancelAfter call is guarded with null +
    /// IsCancellationRequested checks plus a try/catch ObjectDisposedException.
    /// </summary>
    [Fact]
    public async Task OnMessageReceived_After_Dispose_DoesNot_Throw()
    {
        var (client, _) = MakeClientWithMockedTransport();

        // Drive a full request cycle (no response → P2 timeout → finally disposes CTS).
        // We await the timeout path end-to-end so the CTS is genuinely disposed
        // BEFORE we drive the late response — otherwise we'd assert against a
        // still-alive CTS and the test would silently pass even if the guard
        // regressed.
        var p2FiredTcs = new TaskCompletionSource();
        client.OnP2TimeoutFiredForTesting = () => p2FiredTcs.TrySetResult();
        Func<Task> driveTimeout = () => client.SendRequestAsync(0x22, [0x00, 0x01]);
        await driveTimeout.Should().ThrowAsync<UdsException>()
            .WaitAsync(TimeSpan.FromSeconds(2));

        // Wait for the timeout hook to fire (proves the CTS dispose finally
        // block has run and the linked CTS is now disposed). Cap at 500 ms
        // so a regression surfaces quickly instead of hanging.
        (await Task.WhenAny(p2FiredTcs.Task, Task.Delay(500)))
            .Should().Be(p2FiredTcs.Task,
                "OnP2TimeoutFiredForTesting hook must fire so SendRequestInternalAsync's " +
                "finally has disposed the linked CTS by the time we drive the late response");

        // Simulate a late-arriving NRC 0x78 (requestCorrectlyReceivedResponsePending)
        // after the cts is gone. NRC 0x78 is the path that calls cts.CancelAfter(P2*)
        // — that's the call that throws ObjectDisposedException if the CTS has
        // been disposed by SendRequestInternalAsync's finally. A positive response
        // ([0x62, ...]) wouldn't exercise the guard because that path only calls
        // tcs?.TrySetResult (safe no-op when tcs is null after finally).
        Action act = () => client.PublicOnMessageReceivedForTesting([0x7F, 0x22, 0x78]);

        act.Should().NotThrow(
            "OnMessageReceived must tolerate late NRC 0x78 after CTS dispose " +
            "(cts.CancelAfter is the actual throw site)");
    }

    /// <summary>
    /// Thin wrapper exposing the ISO-TP layer's ProcessFrame path so tests
    /// can synthesize late-arriving responses via
    /// <see cref="UdsClient.PublicOnMessageReceivedForTesting"/> without
    /// standing up a fake ICanChannel. The SendFrame callback is captured
    /// here purely so the helper can return both halves.
    /// </summary>
    private sealed class MockTransport
    {
        public IsoTpLayer Iso { get; }
        public MockTransport(IsoTpLayer iso) { Iso = iso; }
    }

    // ========================================================================
    // v1.3.0 MINOR Item 1 (part 2): UdsClient.SecurityAccessAsync must
    // enforce the lockout state set by UdsSecurity. When the level is
    // locked, the call must throw UdsSecurityLockedException WITHOUT
    // touching the wire.
    // ========================================================================

    /// <summary>
    /// v1.3.0 MINOR Item 1: when the security level is locked, the call
    /// must throw UdsSecurityLockedException WITHOUT touching the wire.
    /// </summary>
    [Fact]
    public async Task SecurityAccessAsync_WhenLocked_ThrowsBeforeWireEmit()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);
        // Force lockout for level 0x01 (3 attempts threshold from Default config)
        client.Security.RecordFailedAttempt(0x01);
        client.Security.RecordFailedAttempt(0x01);
        client.Security.RecordFailedAttempt(0x01);
        client.Security.IsLocked(0x01).Should().BeTrue("setup precondition");

        Func<Task> act = () => client.SecurityAccessAsync(level: 0x01, key: null, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UdsSecurityLockedException>(
            "the level is locked; SecurityAccessAsync must throw before any wire emit");
        ex.Which.SecurityLevel.Should().Be(0x01);
        ex.Which.RemainingDelay.Should().BeGreaterThan(TimeSpan.Zero);

        // 业务保证：locked 状态下调用 SecurityAccessAsync 必须先抛异常，
        // 不应触达 wire（SendRequestAsync → _isoTp.SendMessageAsync → sink.Add）。
        // 通过 sent sink 的实际计数来证明这一点，而不是依赖异常类型或超时。
        sent.Should().BeEmpty("locked SecurityAccessAsync must not touch the wire");
    }

    // ========================================================================
    // v1.3.1 PATCH Item 1: lockout counter scope to SendKey leg only.
    // RequestSeed (key is null) receiving NRC 0x35 must NOT increment
    // AttemptCount — failing to obtain a seed is not a security policy
    // violation, only a SendKey auth failure is.
    // ========================================================================

    /// <summary>
    /// v1.3.1 PATCH Item 1: RequestSeed (key=null) NRC 0x35 must not
    /// increment the lockout counter. Lockout is host-side enforcement of
    /// authentication policy; an unsuccessful seed request is not an
    /// authentication attempt.
    /// <para>
    /// Drive RequestSeed three times with NRC 0x35 each time. With v1.3.0
    /// behavior the third attempt trips lockout (AttemptCount hits 3 = the
    /// default <c>MaxAttempts</c>). With the v1.3.1 PATCH the RequestSeed
    /// path is excluded from the counter so the level stays unlocked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SecurityAccessAsync_RequestSeed_Nrc_35_DoesNot_Increment_AttemptCount()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        // Drive three RequestSeed (key=null) cycles, each returning NRC 0x35.
        // For each cycle: kick the request task, yield once so
        // SendRequestAsync registers its pending _responseTcs, then inject
        // the NRC, then await the task and assert UdsNegativeResponseException.
        for (var i = 0; i < 3; i++)
        {
            var requestTask = client.SecurityAccessAsync(level: 0x01, key: null, CancellationToken.None);
            await Task.Yield();
            client.PublicOnMessageReceivedForTesting(
                new byte[] { 0x7F, 0x27, 0x35 });
            Func<Task> act = () => requestTask;
            await act.Should().ThrowAsync<UdsNegativeResponseException>(
                $"the ECU returned NRC 0x35 on RequestSeed attempt {i + 1}");
        }

        // Item 1 invariant: three RequestSeed failures do NOT trip lockout.
        // v1.3.0 would have locked at the 3rd attempt because the catch arm
        // counted every NRC 0x35 regardless of leg. v1.3.1 PATCH excludes
        // RequestSeed (key=null) so the counter stays at 0 and the level
        // remains unlocked.
        client.Security.IsLocked(0x01).Should().BeFalse(
            "RequestSeed failures must not increment host-side lockout counter; " +
            "3 RequestSeed failures should leave the level unlocked");

        // Wire-level side effect: each RequestSeed leg emits exactly one
        // outbound frame (the seed request).
        sent.Should().HaveCount(3, "RequestSeed leg emits one frame per attempt");
    }

    /// <summary>
    /// v1.3.1 PATCH Item 1 (regression guard): SendKey (key!=null) NRC
    /// 0x35 must STILL increment the lockout counter. Only the
    /// RequestSeed path is excluded; the SendKey path keeps existing
    /// semantics from v1.3.0.
    /// <para>
    /// Drive SendKey three times with NRC 0x35 each time. After the third
    /// attempt the level should be locked — this is the pre-fix baseline
    /// preserved by the v1.3.1 PATCH (lockout scope narrowed, but SendKey
    /// leg still counts). Without this regression guard a future refactor
    /// could drop the SendKey path from the counter by accident.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SecurityAccessAsync_SendKey_Nrc_35_Still_Increments_AttemptCount()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        // Drive three SendKey cycles, each returning NRC 0x35.
        for (var i = 0; i < 3; i++)
        {
            var requestTask = client.SecurityAccessAsync(level: 0x01, key: new byte[] { 0xAA }, CancellationToken.None);
            await Task.Yield();
            client.PublicOnMessageReceivedForTesting(
                new byte[] { 0x7F, 0x27, 0x35 });
            Func<Task> act = () => requestTask;
            await act.Should().ThrowAsync<UdsNegativeResponseException>(
                $"the ECU returned NRC 0x35 on SendKey attempt {i + 1}");
        }

        // Regression guard: SendKey failures still count toward lockout.
        // 3 SendKey failures should hit the default MaxAttempts threshold
        // and lock the level — preserving v1.3.0 semantics for the
        // authentication leg.
        client.Security.IsLocked(0x01).Should().BeTrue(
            "3 SendKey failures hit the default MaxAttempts threshold of 3; " +
            "SendKey leg must still count toward lockout");

        sent.Should().HaveCount(3, "SendKey leg emits one frame per attempt");
    }
}

/// <summary>
/// Cross-thread visibility tests for <see cref="UdsClient"/>'s pending
/// response correlation fields (Item 14, v1.2.12 PATCH).
/// <para>
/// <see cref="UdsClient"/>'s internal <c>_responseTcs</c> and
/// <c>_responseCts</c> fields are written by the request thread and read by
/// the <c>OnMessageReceived</c> callback thread. Without <c>volatile</c> /
/// <see cref="Volatile.Read"/>, the JIT may hoist or cache the field across
/// threads, causing lost wake-ups or mismatched response correlation. These
/// tests assert the field is observable as null from a reader thread once
/// the request has finished.
/// </para>
/// </summary>
public sealed class UdsClientVolatileTests
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

    private static void EcuRespond(IsoTpLayer iso, byte[] payload)
    {
        var isoFrame = new IsoTpFrame(IsoTpFrameType.Single, data: payload);
        var canData = isoFrame.Encode();
        iso.ProcessFrame(new CanFrame(
            new CanId(RespId, FrameFormat.Standard),
            canData, FrameFlags.None, default, default));
    }

    private static T? ReadField<T>(UdsClient c, string name) where T : class
    {
        var f = typeof(UdsClient).GetField(
            name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // Thread.MemoryBarrier pairs with the writer's write — without
        // volatile / Volatile.Write, the reader can observe a stale value
        // on weak memory models (ARM, weakly-ordered x86). With the fix,
        // the write is released and the load acquires.
        Thread.MemoryBarrier();
        return (T?)f?.GetValue(c);
    }

    [Fact]
    public async Task ResponseTcs_Assignment_Is_Visible_Across_Threads()
    {
        var (iso, _) = NewIso();
        // v1.2.13 PATCH Item 4: bump P2 so the test's Task.Delay(50) +
        // EcuRespond path doesn't race against the new (correct) P2-timeout
        // cancel-callback that fires TrySetCanceled on _responseTcs.
        // Pre-fix this timeout was silently swallowed; post-fix it
        // unblocks await _responseTcs.Task, which is what we want.
        var timer = new UdsTimer { P2Timeout = TimeSpan.FromSeconds(5) };
        using var client = new UdsClient(iso, timer);

        // The reader thread must observe at least one non-null value (a
        // request was in-flight) and then a null value after the
        // request's finally runs. With the volatile modreq / Volatile.Write
        // the post-finally null is released so any reader acquires it.
        var sawNonNull = false;
        var sawNull = false;
        using var stop = new ManualResetEventSlim(false);
        var reader = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (ReadField<TaskCompletionSource<byte[]>>(client, "_responseTcs") is null)
                {
                    if (sawNonNull) { sawNull = true; return; }
                }
                else
                {
                    sawNonNull = true;
                }
                Thread.Yield();
            }
            stop.Set();
        });

        // Fire a request that completes via a normal positive response.
        var task = client.SendRequestAsync(0x22, new byte[] { 0xF1, 0x90 });
        // Give the reader a moment to observe the non-null state.
        await Task.Delay(50);
        EcuRespond(iso, new byte[] { 0x62, 0xF1, 0x90, 0x41 });
        (await task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Equal(0xF1, 0x90, 0x41);

        await reader.WaitAsync(TimeSpan.FromSeconds(3));
        sawNonNull.Should().BeTrue(
            "reader thread should have observed _responseTcs as non-null while the request was in-flight");
        sawNull.Should().BeTrue(
            "OnMessageReceived reads _responseTcs from the ISO-TP dispatcher thread; " +
            "the field must be volatile so the post-finally null write is observed");
    }

    [Fact]
    public async Task ResponseCts_Assignment_Is_Visible_Across_Threads()
    {
        var (iso, _) = NewIso();
        // See ResponseTcs_Assignment_Is_Visible_Across_Threads above.
        var timer = new UdsTimer { P2Timeout = TimeSpan.FromSeconds(5) };
        using var client = new UdsClient(iso, timer);

        var sawNonNull = false;
        var sawNull = false;
        var reader = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (ReadField<CancellationTokenSource>(client, "_responseCts") is null)
                {
                    if (sawNonNull) { sawNull = true; return; }
                }
                else
                {
                    sawNonNull = true;
                }
                Thread.Yield();
            }
        });

        var task = client.SendRequestAsync(0x22, new byte[] { 0xF1, 0x90 });
        await Task.Delay(50);
        EcuRespond(iso, new byte[] { 0x62, 0xF1, 0x90, 0x41 });
        (await task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Equal(0xF1, 0x90, 0x41);

        await reader.WaitAsync(TimeSpan.FromSeconds(3));
        sawNonNull.Should().BeTrue(
            "reader thread should have observed _responseCts as non-null while the request was in-flight");
        sawNull.Should().BeTrue(
            "SendRequestInternalAsync's finally writes _responseCts = null; " +
            "the field must be volatile so the post-finally null is observable from the reader thread");
    }

    // ========================================================================
    // v1.2.13 PATCH Item 2: production wire-up. The logger-aware ctor must
    // thread ILogger<UdsSession> into the new UdsSession so S3 keepalive
    // failures surface in the production diagnostic log. Legacy 2-arg ctor
    // must keep using parameterless UdsSession (backward compat).
    // ========================================================================

    [Fact]
    public void Ctor_With_SessionLogger_Wires_To_UdsSession()
    {
        // CountingLogger spy — same pattern as UdsSessionTests / IsoTpLayerTests;
        // avoids pulling NSubstitute into Core.Tests.
        var logger = new CountingLogger<UdsSession>();
        var (iso, _) = NewIso();
        var client = new UdsClient(iso, sessionLogger: logger);

        client.Session.SessionLogger.Should().BeSameAs(logger,
            "UdsClient's ILogger<UdsSession> ctor arg must thread into UdsSession");
    }

    [Fact]
    public void Ctor_Without_SessionLogger_Leaves_UdsSession_Logger_Null()
    {
        var (iso, _) = NewIso();
        var client = new UdsClient(iso);

        client.Session.SessionLogger.Should().BeNull(
            "the legacy 2-arg ctor must continue using parameterless UdsSession " +
            "for backward compatibility with v1.2.x callers");
    }

    /// <summary>
    /// v1.2.14 PATCH Item 4: virtual dispatch through TesterPresentAsync must
    /// reach the overridable SendRequestAsync. Without virtual, end-to-end
    /// test doubles (S3 keepalive tests, OEM-specific TesterPresent handlers)
    /// cannot intercept wire-level frame emit without subclassing the entire
    /// UdsClient machinery.
    /// </summary>
    [Fact]
    public async Task TesterPresentAsync_Dispatches_To_SendRequestAsync_Virtual()
    {
        var (iso, _) = NewIso();
        using var spy = new SpyUdsClient(iso);

        await spy.TesterPresentAsync(CancellationToken.None);

        spy.LastSendRequestCall.Should().NotBeNull(
            "TesterPresentAsync must call SendRequestAsync to emit the 0x3E frame");
        spy.LastSendRequestCall!.Value.ServiceId.Should().Be(0x3E,
            "TesterPresent uses SID 0x3E (ISO 14229)");
        spy.LastSendRequestCall.Value.Data.Should().Equal(new byte[] { 0x00 },
            "TesterPresent zero sub-function (no response required)");
        spy.LastSendRequestCall.Value.Cancelled.Should().BeFalse(
            "default CancellationToken is not cancelled");
    }

    /// <summary>
    /// v1.2.14 PATCH Item 4: minimal spy that overrides SendRequestAsync to
    /// record the call arguments instead of going through the ISO-TP wire.
    /// Mirrors the role of existing loggers spies in UdsClientTests — no
    /// NSubstitute dependency.
    /// </summary>
    private sealed class SpyUdsClient : UdsClient
    {
        public (byte ServiceId, byte[]? Data, bool Cancelled)? LastSendRequestCall { get; private set; }

        public SpyUdsClient(IsoTpLayer isoTp) : base(isoTp) { }

        public override Task<byte[]> SendRequestAsync(
            byte serviceId, byte[]? data = null, CancellationToken ct = default)
        {
            LastSendRequestCall = (serviceId, data, ct.IsCancellationRequested);
            // Return empty positive response so TesterPresent's
            // Session.ResetS3Timer() runs cleanly without an actual ECU reply.
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    /// <summary>
    /// Minimal hand-rolled logger spy — same shape as the one in
    /// <c>UdsSessionTests</c>/<c>IsoTpLayerTests</c>. Kept private here
    /// because it is only used by the Item 2 wire-up tests.
    /// </summary>
    private sealed class CountingLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // ========================================================================
    // v1.3.0 MINOR Item 2 + Item 4: EcuResetAsync (0x11) direct tests.
    // Before this task the method had zero direct tests. The three ISO
    // 14229-1 §10.2 standard sub-functions (0x01 HardReset, 0x02
    // KeyOffOnReset, 0x03 SoftReset) must each produce the correct wire
    // frame and return the echoed sub-function. Negative responses must
    // propagate as UdsNegativeResponseException. The enum overload must
    // dispatch to the byte overload with the right cast. The virtual
    // override must be interceptable for test-double scenarios.
    // ========================================================================

    [Fact]
    public async Task EcuResetAsync_HardReset_0x01_Writes_Correct_SID()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.EcuResetAsync(0x01);  // HardReset
        EcuRespond(iso, new byte[] { 0x51, 0x01 });  // positive response

        var resetType = await task.WaitAsync(TimeSpan.FromSeconds(1));

        resetType.Should().Be(0x01, "ECU positive response echoes sub-function 0x01");
        sent.Should().ContainSingle(
            f => f.Length >= 3 && f[1] == 0x11 && f[2] == 0x01,
            "the wire must carry ISO-TP SF with SID 0x11 + sub 0x01 (HardReset)");
    }

    [Fact]
    public async Task EcuResetAsync_KeyOffOnReset_0x02_Writes_Correct_SID()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.EcuResetAsync(0x02);
        EcuRespond(iso, new byte[] { 0x51, 0x02 });

        var resetType = await task.WaitAsync(TimeSpan.FromSeconds(1));

        resetType.Should().Be(0x02);
        sent.Should().ContainSingle(
            f => f.Length >= 3 && f[1] == 0x11 && f[2] == 0x02,
            "wire must carry ISO-TP SF with SID 0x11 + sub 0x02 (KeyOffOnReset)");
    }

    [Fact]
    public async Task EcuResetAsync_SoftReset_0x03_Writes_Correct_SID()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.EcuResetAsync(0x03);
        EcuRespond(iso, new byte[] { 0x51, 0x03 });

        var resetType = await task.WaitAsync(TimeSpan.FromSeconds(1));

        resetType.Should().Be(0x03);
        sent.Should().ContainSingle(
            f => f.Length >= 3 && f[1] == 0x11 && f[2] == 0x03,
            "wire must carry ISO-TP SF with SID 0x11 + sub 0x03 (SoftReset)");
    }

    [Fact]
    public async Task EcuResetAsync_NegativeResponse_Propagates()
    {
        var (iso, _) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.EcuResetAsync(0x01);
        EcuRespond(iso, new byte[] { 0x7F, 0x11, 0x12 });  // NRC 0x12 subFunctionNotSupported

        Func<Task> act = async () => await task;
        await act.Should().ThrowAsync<UdsNegativeResponseException>(
            "NRC 0x12 from ECU must propagate as UdsNegativeResponseException");
    }

    [Fact]
    public async Task EcuResetAsync_EnumOverload_DispatchesToByte()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.EcuResetAsync(UdsResetType.HardReset);
        EcuRespond(iso, new byte[] { 0x51, 0x01 });

        await task.WaitAsync(TimeSpan.FromSeconds(1));

        sent.Should().ContainSingle(
            f => f.Length >= 3 && f[1] == 0x11 && f[2] == (byte)UdsResetType.HardReset,
            "enum overload must call byte overload with correct cast");
    }

    [Fact]
    public async Task EcuResetAsync_VirtualOverride_Interceptable()
    {
        var (iso, _) = NewIso();
        using var spy = new SpyUdsClientForEcuReset(iso);

        var resetType = await spy.EcuResetAsync(0x01);

        spy.LastEcuResetCall.Should().Be(0x01,
            "the virtual override on SpyUdsClientForEcuReset must intercept EcuResetAsync");
        resetType.Should().Be(0xFF,
            "spy returns 0xFF without touching the wire");
    }

    /// <summary>
    /// v1.3.0 MINOR Item 2: minimal spy that overrides EcuResetAsync to
    /// verify virtual dispatch. Mirrors the role of existing SpyUdsClient
    /// (line 537) for TesterPresentAsync / SendRequestAsync.
    /// </summary>
    private sealed class SpyUdsClientForEcuReset : UdsClient
    {
        public byte? LastEcuResetCall { get; private set; }

        public SpyUdsClientForEcuReset(IsoTpLayer isoTp) : base(isoTp) { }

        public override Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
        {
            LastEcuResetCall = resetType;
            return Task.FromResult<byte>(0xFF);
        }
    }

    // ========================================================================
    // v1.3.0 MINOR Item 3: RoutineControlAsync (0x31) direct tests.
    // Before this task the method had only VM-level coverage via
    // RoutinePanelViewModelTests. The three ISO 14229-1 §10.4 standard
    // sub-functions (0x01 StartRoutine, 0x02 StopRoutine, 0x03
    // RequestRoutineResults) must each produce the correct wire frame and
    // return the result bytes (after the [sub, routineIdHigh, routineIdLow]
    // prefix). Short responses must throw UdsException. The enum overload
    // must dispatch to the byte overload with the right cast. The virtual
    // override must be interceptable for test-double scenarios.
    // ========================================================================

    [Fact]
    public async Task RoutineControlAsync_StartRoutine_0x01_Writes_Correct_SID()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.RoutineControlAsync(0x01, routineId: 0x1234);
        EcuRespond(iso, new byte[] { 0x71, 0x01, 0x12, 0x34, 0xAA, 0xBB });  // positive + results

        var result = await task.WaitAsync(TimeSpan.FromSeconds(1));

        result.Should().Equal(new byte[] { 0xAA, 0xBB },
            "RoutineControl result bytes (after SID/sub/routineId prefix)");
        sent.Should().ContainSingle(
            f => f.Length >= 4 && f[1] == 0x31 && f[2] == 0x01 && f[3] == 0x12 && f[4] == 0x34,
            "wire must carry ISO-TP SF with SID 0x31 + sub 0x01 + routineId 0x1234 (StartRoutine)");
    }

    [Fact]
    public async Task RoutineControlAsync_StopRoutine_0x02_Writes_Correct_SID()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.RoutineControlAsync(0x02, routineId: 0xFF00);
        EcuRespond(iso, new byte[] { 0x71, 0x02, 0xFF, 0x00 });  // positive, no result data

        var result = await task.WaitAsync(TimeSpan.FromSeconds(1));

        result.Should().BeEmpty("StopRoutine response carries no result data");
        sent.Should().ContainSingle(
            f => f.Length >= 4 && f[1] == 0x31 && f[2] == 0x02 && f[3] == 0xFF && f[4] == 0x00,
            "wire must carry ISO-TP SF with SID 0x31 + sub 0x02 + routineId 0xFF00 (StopRoutine)");
    }

    [Fact]
    public async Task RoutineControlAsync_RequestRoutineResults_0x03_Writes_Correct_SID()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.RoutineControlAsync(0x03, routineId: 0x0203);
        EcuRespond(iso, new byte[] { 0x71, 0x03, 0x02, 0x03, 0x11, 0x22, 0x33 });

        var result = await task.WaitAsync(TimeSpan.FromSeconds(1));

        result.Should().Equal(new byte[] { 0x11, 0x22, 0x33 });
        sent.Should().ContainSingle(
            f => f.Length >= 4 && f[1] == 0x31 && f[2] == 0x03 && f[3] == 0x02 && f[4] == 0x03,
            "wire must carry ISO-TP SF with SID 0x31 + sub 0x03 + routineId 0x0203 (RequestRoutineResults)");
    }

    [Fact]
    public async Task RoutineControlAsync_ShortResponse_ThrowsUdsException()
    {
        var (iso, _) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.RoutineControlAsync(0x01, routineId: 0x0001);
        EcuRespond(iso, new byte[] { 0x71 });  // too short, missing sub + routineId

        Func<Task> act = async () => await task;
        await act.Should().ThrowAsync<UdsException>(
            "responses < 3 bytes are invalid RoutineControl replies and must throw");
    }

    [Fact]
    public async Task RoutineControlAsync_EnumOverload_DispatchesToByte()
    {
        var (iso, sent) = NewIso();
        using var client = new UdsClient(iso);

        var task = client.RoutineControlAsync(RoutineControlType.StopRoutine, routineId: 0x0001);
        EcuRespond(iso, new byte[] { 0x71, 0x02, 0x00, 0x01 });

        await task.WaitAsync(TimeSpan.FromSeconds(1));

        sent.Should().ContainSingle(
            f => f.Length >= 4 && f[1] == 0x31 && f[2] == (byte)RoutineControlType.StopRoutine,
            "enum overload must call byte overload with correct cast");
    }

    [Fact]
    public async Task RoutineControlAsync_VirtualOverride_Interceptable()
    {
        var (iso, _) = NewIso();
        using var spy = new SpyUdsClientForRoutineControl(iso);

        var result = await spy.RoutineControlAsync(0x01, routineId: 0x1234);

        spy.LastRoutineControlCall!.Value.Should().Be((0x01, (ushort)0x1234),
            "the virtual override must intercept RoutineControlAsync");
        result.Should().Equal(new byte[] { 0xDE, 0xAD },
            "spy returns canned result without touching the wire");
    }

    /// <summary>
    /// v1.3.0 MINOR Item 3: minimal spy that overrides RoutineControlAsync
    /// to verify virtual dispatch. Mirrors the role of SpyUdsClientForEcuReset
    /// (above) for EcuResetAsync.
    /// </summary>
    private sealed class SpyUdsClientForRoutineControl : UdsClient
    {
        public (byte Sub, ushort RoutineId)? LastRoutineControlCall { get; private set; }

        public SpyUdsClientForRoutineControl(IsoTpLayer isoTp) : base(isoTp) { }

        public override Task<byte[]> RoutineControlAsync(
            byte routineControlType, ushort routineId,
            byte[]? data = null, CancellationToken ct = default)
        {
            LastRoutineControlCall = (routineControlType, routineId);
            return Task.FromResult(new byte[] { 0xDE, 0xAD });
        }
    }
}
