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
}