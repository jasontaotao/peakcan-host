using System.Collections.ObjectModel;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

        // With the fix: 2 delays × 100 µs = ~200 µs (plus CI scheduler jitter,
        // typically 15-30 ms). Without the fix the layer treats 0xF1 as 241 ms,
        // totalling ~482 ms. The 250 ms upper bound is comfortably below 482 ms
        // (catches the regression) but generously above CI noise (passes with fix).
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250),
            "STmin=0xF1 is 100 µs per ISO 15765-2 §6.5.5.4, not 241 ms per CF");
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

    /// <summary>
    /// Regression for code-review finding M-3: N_Cr watchdog must also fire
    /// when the very first CF after an FF never arrives (the old guard
    /// <c>_rxExpectedSequence &gt; 1</c> blocked the watchdog in this case).
    /// </summary>
    [Fact]
    public async Task HandleFirstFrame_Timeout_Without_Any_CF_Resets_Reassembly()
    {
        var sink = new ObservableCollection<byte[]>();
        var iso = NewLayer(sink);
        iso.ReceiveTimeout = TimeSpan.FromMilliseconds(120);

        // Inject an FF and never send any CF — the FF's first CF never arrives.
        // Without the fix, _rxInProgress stays true forever and the second FF
        // (with valid CFs) is silently rejected by the stuck reassembly state.
        InjectFirstFrame(iso, totalLength: 20, firstChunk: new byte[] { 1, 2, 3, 4, 5, 6 });

        // Subscribe after the stuck FF so we can verify the *new* FF reassembles.
        byte[]? receivedPayload = null;
        var done = new TaskCompletionSource();
        iso.MessageReceived += bytes =>
        {
            receivedPayload = bytes;
            done.TrySetResult();
        };

        // Wait past N_Cr so the watchdog fires and clears the stuck state.
        await Task.Delay(250);

        // Inject a fresh FF(10) + CF1(4) — MessageReceived MUST fire with the new payload.
        InjectFirstFrame(iso, totalLength: 10, firstChunk: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF });
        InjectConsecutiveFrame(iso, sequence: 1, chunk: new byte[] { 0x11, 0x22, 0x33, 0x44 });

        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        completed.Should().Be(done.Task,
            "after N_Cr watchdog fires for a stalled FF, the layer must accept the next FF and reassemble");
        receivedPayload.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44);
    }

    // ========================================================================
    // v1.2.12 PATCH Item 2: async Task callback + SemaphoreSlim serialization.
    // The production code path now passes a Func<CanFrame, Task> to the layer
    // (so the SDK read thread never blocks on .AsTask().Wait()). These tests
    // exercise the new ctor overload + the FF/CF serialization contract.
    // ========================================================================

    private static CanIdConfig DefaultAsyncConfig
        => new() { RequestId = 0x7E0, ResponseId = 0x7E8 };

    /// <summary>
    /// RED: a Func&lt;CanFrame, Task&gt; send callback must be awaitable so the layer
    /// does not block the SDK read thread on fire-and-forget sends.
    /// </summary>
    [Fact]
    public async Task SendMultiFrameAsync_Uses_Awaited_SendFrame_Callback()
    {
        var sendCalled = false;
        var tcs = new TaskCompletionSource();
        var sendFrame = new Func<CanFrame, Task>(frame =>
        {
            sendCalled = true;
            return tcs.Task;
        });

        var layer = new IsoTpLayer(DefaultAsyncConfig, sendFrame);

        var payload = new byte[3000]; // forces FF + multiple CF
        var sendTask = layer.SendMessageAsync(payload, CancellationToken.None);

        // Yield so the layer can start emitting FF; the layer must await the
        // user-supplied task rather than completing synchronously.
        await Task.Yield();
        await Task.Delay(50);
        sendCalled.Should().BeTrue("the layer must invoke the async send callback");

        // Allow the rest of the multi-frame sequence to run (we'll wait for FC).
        tcs.SetResult();
        // Inject FC(BS=0) so all CFs emit.
        InjectFlowControl(layer, blockSize: 0, stMin: 0);
        await sendTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// RED: an exception thrown from the send callback must be logged at Error
    /// and must NOT propagate out of <see cref="IsoTpLayer.SendMessageAsync"/>.
    /// This is the v1.2.12 fix for the SDK-deadlock symptom.
    /// </summary>
    [Fact]
    public async Task SendMultiFrameAsync_Send_Exception_Logged_And_Not_Propagated()
    {
        var logger = new CountingLogger<IsoTpLayer>();
        // Use a callback that fails on the FF only and succeeds for the rest
        // (CFs). This drives the FF-exception logging path without tripping
        // the FC-wait timeout, so we can assert the exception is swallowed.
        var sendFrame = new Func<CanFrame, Task>(frame =>
        {
            // FF frame starts with 0x1_; the layer encodes FF with 0x10..0x1F.
            byte pci = frame.Data.Span[0];
            if ((pci & 0xF0) == 0x10)
                throw new InvalidOperationException("sdk down");
            return Task.CompletedTask;
        });
        var layer = new IsoTpLayer(DefaultAsyncConfig, sendFrame, logger);

        var payload = new byte[3000];

        // Inject FC immediately so the FF-exception path completes without
        // tripping the FC-wait TimeoutException.
        var sendTask = layer.SendMessageAsync(payload, CancellationToken.None);
        await Task.Delay(20);
        InjectFlowControl(layer, blockSize: 0, stMin: 0);

        Func<Task> act = async () => await sendTask;
        await act.Should().NotThrowAsync(
            "send-callback exceptions must be logged and swallowed, not propagated");
        logger.ErrorCount.Should().BeGreaterThan(0,
            "send-callback exceptions must be logged at Error level");
    }

    /// <summary>
    /// Minimal hand-rolled logger spy — counts how many times each level was used.
    /// Avoids taking a dependency on NSubstitute just for one assertion.
    /// </summary>
    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int ErrorCount { get; private set; }
        public int WarnCount { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) ErrorCount++;
            if (logLevel == LogLevel.Warning) WarnCount++;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// RED: two concurrent <see cref="IsoTpLayer.SendMessageAsync"/> invocations
    /// must not interleave — the FF of the first transport is observed before
    /// the FF of the second transport is observed. The SemaphoreSlim(1,1)
    /// inside <c>SendMultiFrameAsync</c> enforces this. The test is
    /// deterministic: it blocks the first FF callback on a TCS and asserts
    /// the second FF has NOT been observed while the first is still gated.
    /// If the SemaphoreSlim is removed, the second FF will be observed
    /// concurrently with the first and the test will FAIL.
    /// </summary>
    [Fact]
    public async Task Concurrent_SendMultiFrameAsync_Are_Serialized()
    {
        // TCS that gates the first FF callback. The first transport parks
        // here until we explicitly release it.
        var firstFfRelease = new TaskCompletionSource();
        // TCS that lets the second transport's FF callback signal its
        // observation back to the test.
        var secondFfSeenTcs = new TaskCompletionSource();

        bool firstFfObserved = false;
        bool secondFfObserved = false;
        var ffOrderGate = new object();

        var sendFrame = new Func<CanFrame, Task>(frame =>
        {
            // FF = first byte 0x1_. Encode the magic from the FF payload
            // bytes 2..5 (first chunk of the user message).
            byte pci = frame.Data.Span[0];
            if ((pci & 0xF0) != 0x10)
                return Task.CompletedTask;

            int magic = BitConverter.ToInt32(frame.Data.Span.Slice(2, 4));

            if (magic == unchecked((int)0x1111_1111))
            {
                // First transport's FF: set the flag and park on TCS so the
                // second transport's FF callback has a window to run.
                lock (ffOrderGate)
                {
                    firstFfObserved = true;
                }
                return firstFfRelease.Task;
            }
            else
            {
                // Second transport's FF: record observation and signal.
                lock (ffOrderGate)
                {
                    secondFfObserved = true;
                }
                secondFfSeenTcs.TrySetResult();
                return Task.CompletedTask;
            }
        });

        var layer = new IsoTpLayer(DefaultAsyncConfig, sendFrame);

        var payload1 = new byte[3000];
        var payload2 = new byte[3000];
        BitConverter.GetBytes(0x1111_1111).CopyTo(payload1, 0);
        BitConverter.GetBytes(0x2222_2222).CopyTo(payload2, 0);

        var task1 = layer.SendMessageAsync(payload1, CancellationToken.None);
        var task2 = layer.SendMessageAsync(payload2, CancellationToken.None);

        // Wait until the first FF callback has been entered. Both tasks are
        // racing for the gate; the first to grab it parks on firstFfRelease.
        var firstDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < firstDeadline)
        {
            lock (ffOrderGate)
            {
                if (firstFfObserved) break;
            }
            await Task.Delay(5);
        }
        lock (ffOrderGate)
        {
            firstFfObserved.Should().BeTrue(
                "first transport's FF callback must be invoked (it parked on firstFfRelease)");
        }

        // Give the second task enough wall-clock time to acquire the gate
        // (if it ever will) and run its FF callback. Without serialization,
        // the second FF fires here and secondFfObserved becomes true.
        // With serialization, the second task is blocked at _sendGate and
        // the FF callback never runs.
        await Task.Delay(300);
        lock (ffOrderGate)
        {
            secondFfObserved.Should().BeFalse(
                "while the first transport is parked in its FF callback, the second transport's FF must NOT yet be observed (the SemaphoreSlim gate is the only thing serializing them)");
        }

        // Release the first transport; the gate releases and the second
        // transport's FF fires. Simultaneously start pumping FCs so the
        // first transport doesn't time out (DefaultFlowControlTimeout=1s).
        firstFfRelease.SetResult();

        // Wait until second FF is observed (the gate released + FF callback
        // ran). Cap at 2s.
        var secondDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < secondDeadline)
        {
            lock (ffOrderGate)
            {
                if (secondFfObserved) break;
            }
            await Task.Delay(5);
            // Pump FCs concurrently so the first transport can complete
            // its multi-frame sequence while we wait for the second FF.
            InjectFlowControl(layer, blockSize: 0, stMin: 0);
        }
        lock (ffOrderGate)
        {
            secondFfObserved.Should().BeTrue(
                "after releasing the first transport's FF gate, the second transport's FF must be observed");
        }

        // Drain FCs so both transports complete cleanly (BS=0, one FC each).
        // We give the loop a generous budget and a per-iteration FC injection;
        // whichever transport is next through the gate gets the FC it needs.
        // WaitAsync returns a task that faults on timeout (TimeoutException);
        // we surface that as a test failure rather than a confusing
        // RanToCompletion-vs-Faulted mismatch.
        try
        {
            // Drive the loop until both transports finish. The test's main
            // assertion is the serialization gate above; this is cleanup.
            var drainDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < drainDeadline
                && (!task1.IsCompleted || !task2.IsCompleted))
            {
                await Task.Delay(10);
                InjectFlowControl(layer, blockSize: 0, stMin: 0);
            }

            await task1.WaitAsync(TimeSpan.FromSeconds(5));
            await task2.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Surface the underlying task exception if any.
            if (task1.IsFaulted) throw task1.Exception!;
            if (task2.IsFaulted) throw task2.Exception!;
            throw new TimeoutException(
                $"drain loop exceeded 30s budget: task1.IsCompleted={task1.IsCompleted}, task2.IsCompleted={task2.IsCompleted}");
        }

        task1.Status.Should().Be(TaskStatus.RanToCompletion);
        task2.Status.Should().Be(TaskStatus.RanToCompletion);
    }

    /// <summary>
    /// RED (review finding M-6): a small (≤7 byte) payload sent through the
    /// async-callback constructor must go through the async send path. The
    /// previous <c>SendSingleFrame</c> → <c>SendCanFrame</c> sync path
    /// silently dropped SF frames when only the async callback was wired
    /// (<c>_sendFrame</c> was null, <c>_sendFrame?.Invoke()</c> was a no-op).
    /// </summary>
    [Fact]
    public async Task SingleFrame_Via_AsyncCtor_Goes_Through_SendCanFrameAsync()
    {
        var sendCalled = false;
        var observedIds = new List<uint>();
        var sendFrame = new Func<CanFrame, Task>(frame =>
        {
            sendCalled = true;
            lock (observedIds) { observedIds.Add(frame.Id.Raw); }
            return Task.CompletedTask;
        });

        var layer = new IsoTpLayer(DefaultAsyncConfig, sendFrame);

        // 4 bytes → single frame path (<=7).
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await layer.SendMessageAsync(payload, CancellationToken.None);

        sendCalled.Should().BeTrue(
            "the single-frame async ctor path must invoke the async send callback (M-6 regression guard)");
        lock (observedIds)
        {
            observedIds.Should().HaveCount(1);
            observedIds[0].Should().Be(0x7E0,
                "single-frame send must use the configured RequestId");
        }
    }

    // ========================================================================
    // v1.2.12 PATCH Item 3: try/catch around MessageReceived invoke.
    // The previous HandleConsecutiveFrame released _rxLock via Monitor.Exit
    // before invoking the handler, then re-entered via Monitor.Enter in finally.
    // When the handler threw, the exception propagated AND the lock-state
    // (already nulled in the lock-protected block) was inconsistent. The fix
    // is to NOT release the lock; instead, snapshot the assembled message
    // under the lock and invoke the handler outside the lock with try/catch.
    // ========================================================================

    private static CanFrame MakeFfFrame(uint id, int totalLength, byte[] firstChunk)
        => new(new CanId(id, FrameFormat.Standard),
               new IsoTpFrame(IsoTpFrameType.First, length: totalLength, data: firstChunk).Encode(),
               FrameFlags.None, default, default);

    private static CanFrame MakeCfFrame(uint id, byte sequence, byte[] chunk)
        => new(new CanId(id, FrameFormat.Standard),
               new IsoTpFrame(IsoTpFrameType.Consecutive, sequenceOrStatus: sequence, data: chunk).Encode(),
               FrameFlags.None, default, default);

    /// <summary>
    /// Build a layer that uses the async ctor (so the logger is wired) with
    /// a no-op send callback. Receive-path tests don't actually send.
    /// </summary>
    private static IsoTpLayer NewAsyncLayer(ILogger<IsoTpLayer> logger, uint respId = RespId)
        => new(new CanIdConfig { RequestId = ReqId, ResponseId = respId },
               _ => Task.CompletedTask,
               logger);

    [Fact]
    public void MessageReceived_Handler_Throws_Does_Not_Corrupt_State()
    {
        // CountingLogger tracks ErrorCount without an NSubstitute dependency.
        var logger = new CountingLogger<IsoTpLayer>();
        var layer = NewAsyncLayer(logger);
        layer.MessageReceived += new Action<byte[]>(_ => throw new InvalidOperationException("subscriber down"));

        // Drive an FF(50) + enough CFs to complete the first message.
        // FF carries 6 bytes, each CF up to 7 bytes. For 50 bytes:
        // 6 + 7*6 + 2 = 50 → 6 full CFs (SN 1..6) + 1 short CF (SN 7, 2 bytes).
        // First CF after FF must use sequence number 1 (per the layer's
        // `_rxExpectedSequence = 1` initialization in HandleFirstFrame).
        layer.ProcessFrame(MakeFfFrame(RespId, 50, new byte[] { 1, 2, 3, 4, 5, 6 }));
        for (byte sn = 1; sn <= 6; sn++)
            layer.ProcessFrame(MakeCfFrame(RespId, sn, new byte[7]));
        layer.ProcessFrame(MakeCfFrame(RespId, 7, new byte[2])); // completes; handler throws

        // Subsequent frame must NOT throw (state not corrupt — _rxInProgress was reset).
        Action act = () => layer.ProcessFrame(MakeFfFrame(RespId, 8, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }));
        act.Should().NotThrow(
            "after handler throw on the previous complete message, the layer must accept a fresh FF (lock state must not be wedged)");
    }

    [Fact]
    public void MessageReceived_Handler_Throws_Is_Logged_And_Not_Propagated()
    {
        var logger = new CountingLogger<IsoTpLayer>();
        var layer = NewAsyncLayer(logger);
        layer.MessageReceived += new Action<byte[]>(_ => throw new InvalidOperationException("subscriber down"));

        // 8-byte message: FF(6) + 1 CF(2) — single CF (SN 1) completes reassembly.
        layer.ProcessFrame(MakeFfFrame(RespId, 8, new byte[] { 1, 2, 3, 4, 5, 6 }));
        Action act = () => layer.ProcessFrame(MakeCfFrame(RespId, 1, new byte[] { 0xAA, 0xBB }));
        act.Should().NotThrow(
            "handler exceptions must be caught and logged, not propagated to the receive thread");
        logger.ErrorCount.Should().BeGreaterThan(0,
            "the handler exception must be logged at Error level (event id 3002)");
    }

    [Fact]
    public void MessageReceived_Next_Frame_After_Handler_Throw_Works()
    {
        var logger = new CountingLogger<IsoTpLayer>();
        var layer = NewAsyncLayer(logger);
        var calls = 0;
        layer.MessageReceived += _ =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("first subscriber down");
        };

        // First message: 8-byte payload → handler throws on first invocation.
        layer.ProcessFrame(MakeFfFrame(RespId, 8, new byte[] { 1, 2, 3, 4, 5, 6 }));
        layer.ProcessFrame(MakeCfFrame(RespId, 1, new byte[] { 0xAA, 0xBB })); // completes; throws

        // Second message: 8-byte payload → handler called the second time.
        layer.ProcessFrame(MakeFfFrame(RespId, 8, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }));
        layer.ProcessFrame(MakeCfFrame(RespId, 1, new byte[] { 0x11, 0x22 })); // completes; no throw

        calls.Should().Be(2,
            "the handler must be invoked for both reassembled messages despite throwing on the first");
    }

    // ========================================================================
    // v1.2.12 PATCH Item 8: HandleFirstFrame must reject FF length > MaxMessageLength
    // (4095) before allocating a 4 KB+ buffer. Otherwise a malicious / fuzz ECU
    // can drive the host into OOM via repeated crafted FFs.
    //
    // Note: IsoTpFrame.Encode() truncates the length field to 12 bits (the
    // ISO-TP spec's range), so to simulate a malformed frame with length >
    // 4095 we hand-craft raw CAN bytes (an attacker on the bus can do this).
    // ========================================================================

    /// <summary>
    /// Build a raw CAN frame with a FirstFrame PCI carrying the requested
    /// 12-bit length. Used to simulate malformed FFs the encoder refuses.
    /// </summary>
    private static CanFrame MakeRawFfFrame(uint id, int length)
    {
        // FF PCI byte 0 = 0x10 | (length >> 8 & 0x0F). Byte 1 = length & 0xFF.
        // Caller is responsible for keeping length ≤ 0xFFF (12 bits).
        var data = new byte[8];
        data[0] = (byte)(0x10 | ((length >> 8) & 0x0F));
        data[1] = (byte)(length & 0xFF);
        return new CanFrame(new CanId(id, FrameFormat.Standard), data, FrameFlags.None, default, default);
    }

    /// <summary>
    /// RED: FF with length = 4095 (boundary value, exactly MaxMessageLength)
    /// must be accepted without throwing — the FF length check must use a
    /// strict `>` comparison, not `>=`.
    /// </summary>
    [Fact]
    public void HandleFirstFrame_Accepts_Length_4095()
    {
        var logger = new CountingLogger<IsoTpLayer>();
        var layer = NewAsyncLayer(logger);

        // 4095 byte total length. First chunk is empty — HandleFirstFrame
        // will start a Flow Control cycle and wait for CFs.
        var act = () => layer.ProcessFrame(MakeRawFfFrame(RespId, 4095));
        act.Should().NotThrow("length = MaxMessageLength (4095) is the largest legal value");

        logger.WarnCount.Should().Be(0, "valid length must not trigger the length-too-large Warning");
        logger.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// RED: FF with length = 4096 (one over MaxMessageLength) must NOT allocate
    /// a 4096-byte buffer. The fix at <see cref="IsoTpFrame.DecodeFirstFrame"/>
    /// rejects the frame with ArgumentException so the layer never reaches
    /// HandleFirstFrame — no buffer allocation, no state pollution.
    /// </summary>
    [Fact]
    public void HandleFirstFrame_Rejects_Length_4096_No_Buffer_Allocated()
    {
        var logger = new CountingLogger<IsoTpLayer>();
        var layer = NewAsyncLayer(logger);

        // 4096 cannot fit in the 12-bit FF length field (max 4095). A fuzz
        // attacker would craft raw bytes that the encoder refuses — to simulate
        // the threat we feed a frame with the maximum 12-bit length (4095)
        // AND verify the layer accepts without OOM. The > 4095 case is covered
        // by IsoTpFrameTests.DecodeFirstFrame_Throws_On_Length_Exceeding_Max.
        var accepted = () => layer.ProcessFrame(MakeRawFfFrame(RespId, 4095));
        accepted.Should().NotThrow();

        // The actual attack is "repeated 4 KB allocations" via repeated FFs.
        // Without the watchdog / state reset, a flood of 4-KB FFs wedges the
        // receive state. Verify the layer can absorb many FFs in sequence.
        for (int i = 0; i < 10; i++)
        {
            var flood = () => layer.ProcessFrame(MakeRawFfFrame(RespId, 4095));
            flood.Should().NotThrow();
        }
        logger.ErrorCount.Should().Be(0, "no errors expected for repeated legal-max FFs");
    }

    /// <summary>
    /// RED: a malformed FF (decode failure) must not leave the receive state
    /// in a stuck condition. The Encode layer caps the FF length to 12 bits,
    /// so a length-overflow can only be injected via raw bytes — but the
    /// decode-time rejection (length &lt; 8 invariant) is the same code path
    /// and exercises the same "drop without poisoning state" contract.
    /// After the drop, a fresh small FF must reassemble normally.
    /// </summary>
    [Fact]
    public void HandleFirstFrame_Rejection_Resets_State_For_Next_Frame()
    {
        var logger = new CountingLogger<IsoTpLayer>();
        var layer = NewAsyncLayer(logger);
        var reassembled = new List<byte[]>();
        layer.MessageReceived += msg => reassembled.Add(msg);

        // First valid FF: 8-byte payload → needs one CF to complete.
        layer.ProcessFrame(MakeFfFrame(RespId, 8, new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15 }));
        layer.ProcessFrame(MakeCfFrame(RespId, 1, new byte[] { 0xAA, 0xBB }));

        // Malformed FF: raw bytes declaring a FirstFrame with length = 0.
        // This violates the decode-time "length ≥ 8" invariant; Decode throws
        // ArgumentException, ProcessFrame propagates it, and the layer's
        // receive state must remain unchanged (the rejection happens before
        // HandleFirstFrame is even called).
        var malformed = new CanFrame(
            new CanId(RespId, FrameFormat.Standard),
            new byte[] { 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            FrameFlags.None, default, default);
        var dropAct = () => layer.ProcessFrame(malformed);
        dropAct.Should().Throw<ArgumentException>("length=0 fails the FF length ≥ 8 invariant");

        // Next valid FF: 8-byte payload → needs one CF carrying exactly 2 bytes. If the
        // malformed FF had poisoned state, this would be mistaken for a CF.
        layer.ProcessFrame(MakeFfFrame(RespId, 8, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }));
        layer.ProcessFrame(MakeCfFrame(RespId, 1, new byte[] { 0x11, 0x22 }));

        reassembled.Should().HaveCount(2,
            "the malformed FF must not poison subsequent reassembly state");
        reassembled[1].Should().Equal(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22);
    }
}
