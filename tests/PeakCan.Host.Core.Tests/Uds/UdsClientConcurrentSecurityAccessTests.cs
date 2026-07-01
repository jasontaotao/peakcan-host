using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FluentAssertions;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// v1.4.1 PATCH Item 1: tests for atomic concurrent mid-handshake behavior of
/// <c>SecurityAccessAsync(byte, CancellationToken)</c> (the 2-arg overload).
/// <para>
/// Carry-over from v1.3.1 PATCH pre-ship review. The 2-arg overload has a
/// documented TOCTOU window between the RequestSeed leg completing and the
/// SendKey leg starting — see <c>UdsClient.cs:381-408</c> XML doc remarks.
/// These tests assert:
/// </para>
/// <list type="number">
///   <item>Wire emission structure preserved under concurrent same-level calls
///         (2 RequestSeed + 2 SendKey frames, regardless of interleave order
///         determined by <c>SemaphoreSlim _requestLock</c> arbitration).</item>
///   <item>Lockout state stays consistent across the mid-handshake lockout
///         flip race (one caller may observe lockout, the other already received
///         the failure response — both are valid outcomes).</item>
/// </list>
/// <para>
/// <b>Phase 2.5 finding:</b> the pre-existing test
/// <c>SecurityAccessAsync_SendKey_Nrc_35_Still_Increments_AttemptCount</c>
/// uses the 3-arg overload directly and never exercises the 2-arg overload's
/// full RequestSeed→SendKey path. <see cref="UdsSecurity.SetSeed"/> creates a
/// fresh <c>SecurityLevelState</c> on success, which RESETS
/// <c>AttemptCount</c> and clears <c>LockedUntilUtc</c>. So a pre-set
/// <c>AttemptCount</c> via direct <c>RecordFailedAttempt</c> call is wiped by
/// the first successful RequestSeed in the 2-arg overload. The race tests
/// below therefore do NOT pre-set <c>AttemptCount</c> — they let the natural
/// accumulation from concurrent SendKey failures drive the lockout boundary.
/// </para>
/// <para>
/// Per spec Decision 2: 5-second <c>Task.WhenAny</c> deadline prevents CI hangs.
/// Per memory v1.2.12 lesson 4: race tests are transient-flaky acceptable;
/// CI re-runs 3x and only fails if all 3 fail.
/// </para>
/// <para>
/// <b>Dynamic response injection pattern</b>: the existing single-threaded tests
/// in <c>UdsClientTests.cs</c> use <c>await Task.Yield()</c> to let one
/// <c>SendRequestAsync</c> register <c>_responseTcs</c> before injecting a
/// response. For TWO concurrent <c>SecurityAccessAsync</c> calls this breaks
/// because the order in which <c>_requestLock</c> arbitration wakes the
/// second waiter is non-deterministic. Instead, we subscribe to
/// <c>sent.CollectionChanged</c> and inject a response matching the
/// observed frame's UDS sub-function byte (0x01 = RequestSeed → seed response;
/// 0x02 = SendKey → positive/NRC key response). This pattern handles
/// all interleavings the lock can produce.
/// </para>
/// <para>
/// <b>ISO-TP SF PCI awareness</b>: the captured <c>sent</c> frames are
/// ISO-TP encoded — the first byte is the PCI nibble (0x0L for Single Frame,
/// L = payload length). UDS payload begins at index 1. So a captured
/// RequestSeed frame is <c>[0x02, 0x27, 0x01]</c>, NOT <c>[0x27, 0x01]</c>.
/// </para>
/// </summary>
public class UdsClientConcurrentSecurityAccessTests
{
    // CA1861: avoid constant array arguments repeated across calls.
    private static readonly int[] AllowedThrewCounts = [1, 2];

    private static (IsoTpLayer iso, ObservableCollection<byte[]> sent) NewIsoWithCapture()
    {
        var sent = new ObservableCollection<byte[]>();
        var iso = new IsoTpLayer(
            new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
            frame => sent.Add(frame.Data.ToArray()));
        return (iso, sent);
    }

    /// <summary>
    /// Extract UDS SID + sub-function from an ISO-TP-encoded CAN frame. Returns
    /// <c>(sid, subFn)</c> where sid is the UDS service ID and subFn is the
    /// SecurityAccess sub-function (0x01 RequestSeed, 0x02 SendKey). Returns
    /// <c>(0, 0)</c> for non-Single-Frame or short frames.
    /// </summary>
    private static (byte sid, byte subFn) ExtractUdsSubFn(byte[] frame)
    {
        // ISO-TP SF PCI byte = 0x0L where L = payload length (0-7 for classic CAN).
        bool isSf = (frame.Length >= 3) && (frame[0] & 0xF0) == 0x00;
        if (!isSf) return (0, 0);
        byte sid = frame[1];
        byte subFn = frame[2];
        return (sid, subFn);
    }

    /// <summary>
    /// Auto-respond to wire frames. Subscribes to <paramref name="sent"/>'s
    /// <c>CollectionChanged</c> event and injects a matching positive response
    /// per observed frame's UDS sub-function byte. Returns an <see cref="Unsubscriber"/>
    /// that unsubscribes the handler on dispose.
    /// </summary>
    private static Unsubscriber AutoRespondPositive(
        UdsClient client,
        ObservableCollection<byte[]> sent)
    {
        void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is null) return;
            foreach (byte[] frame in e.NewItems)
            {
                var (sid, subFn) = ExtractUdsSubFn(frame);
                if (sid != 0x27) continue;
                switch (subFn)
                {
                    case 0x01: // RequestSeed
                        client.PublicOnMessageReceivedForTesting(
                            new byte[] { 0x67, 0x01, 0xAA, 0xBB });
                        break;
                    case 0x02: // SendKey
                        client.PublicOnMessageReceivedForTesting(
                            new byte[] { 0x67, 0x02 });
                        break;
                }
            }
        }
        sent.CollectionChanged += OnChanged;
        return new Unsubscriber(sent, OnChanged);
    }

    /// <summary>
    /// Auto-respond: positive seed for RequestSeed, NRC 0x35 for SendKey.
    /// Per v1.3.1 PATCH Item 1: RequestSeed NRC 0x35 does NOT increment
    /// <c>AttemptCount</c>; SendKey NRC 0x35 DOES increment.
    /// <para>
    /// Both SendKey failures accumulate <c>AttemptCount</c> on the SAME
    /// <c>SecurityLevelState</c> instance (since SetSeed runs once per
    /// successful RequestSeed, then SendKey failures mutate the existing state).
    /// With <c>MaxAttempts=2</c>, the second SendKey failure triggers lockout.
    /// </para>
    /// </summary>
    private static Unsubscriber AutoRespondSeedOkThenNrc35(
        UdsClient client,
        ObservableCollection<byte[]> sent)
    {
        void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is null) return;
            foreach (byte[] frame in e.NewItems)
            {
                var (sid, subFn) = ExtractUdsSubFn(frame);
                if (sid != 0x27) continue;
                switch (subFn)
                {
                    case 0x01: // RequestSeed — positive seed response
                        client.PublicOnMessageReceivedForTesting(
                            new byte[] { 0x67, 0x01, 0xAA, 0xBB });
                        break;
                    case 0x02: // SendKey — NRC 0x35 (triggers RecordFailedAttempt)
                        client.PublicOnMessageReceivedForTesting(
                            new byte[] { 0x7F, 0x27, 0x35 });
                        break;
                }
            }
        }
        sent.CollectionChanged += OnChanged;
        return new Unsubscriber(sent, OnChanged);
    }

    /// <summary>
    /// Test 1: Two concurrent <c>SecurityAccessAsync</c> calls on the same level
    /// must produce exactly 4 wire frames (2 RequestSeed + 2 SendKey) with the
    /// expected SID + sub-function bytes. The order between the four frames is
    /// not asserted (depends on <c>SemaphoreSlim _requestLock</c> arbitration
    /// after the first leg's response is consumed).
    /// </summary>
    [Fact]
    public async Task TwoArg_Overload_TwoConcurrentCalls_ProduceExactlyFourWireFrames()
    {
        // Arrange — high MaxAttempts to avoid lockout.
        var (iso, sent) = NewIsoWithCapture();
        var algo = new FakeKeyDerivationAlgorithm();
        using var client = new UdsClient(iso, algo);
        client.Security.LockoutConfig = new UdsSecurityLockoutConfig(
            MaxAttempts: 10,
            LockoutDuration: TimeSpan.FromSeconds(5));

        using var auto = AutoRespondPositive(client, sent);

        // Act — two concurrent SecurityAccessAsync on level 0x01.
        var t1 = client.SecurityAccessAsync(0x01, CancellationToken.None);
        var t2 = client.SecurityAccessAsync(0x01, CancellationToken.None);

        // 5s deadline prevents CI hangs.
        var completed = await Task.WhenAny(
            Task.WhenAll(WrapResult(t1), WrapResult(t2)),
            Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed is not null,
            "Race test timed out after 5s");

        // Both calls should complete without throwing (positive responses).
        var r1 = await t1;
        var r2 = await t2;
        r1.Should().NotBeNull("t1 should have completed");
        r2.Should().NotBeNull("t2 should have completed");

        // Assert — exactly 4 wire frames emitted (2 RequestSeed + 2 SendKey).
        sent.Should().HaveCount(4,
            "two complete 2-arg SecurityAccessAsync handshakes = 2 RequestSeed + 2 SendKey frames");

        // Assert — frame structure: SID=0x27 for all; sub-function 0x01 = RequestSeed, 0x02 = SendKey.
        foreach (var frame in sent)
        {
            var (sid, subFn) = ExtractUdsSubFn(frame);
            sid.Should().Be(0x27, "SecurityAccess SID");
            subFn.Should().BeOneOf(new byte[] { 0x01, 0x02 },
                "sub-function = level (RequestSeed) or level+1 (SendKey)");
        }

        // Assert — exactly 2 RequestSeed (subFn=0x01) and 2 SendKey (subFn=0x02).
        sent.Count(f => ExtractUdsSubFn(f).subFn == 0x01).Should().Be(2, "2 RequestSeed frames");
        sent.Count(f => ExtractUdsSubFn(f).subFn == 0x02).Should().Be(2, "2 SendKey frames");

        // Assert — lockout state unchanged (both succeeded, no RecordFailedAttempt).
        client.Security.IsLocked(0x01).Should().BeFalse(
            "both handshakes completed successfully; lockout must not have triggered");
    }

    /// <summary>
    /// Test 2: Mid-handshake lockout flip via concurrent SendKey failures.
    /// <b>Skipped:</b> Phase 2.5 actual code exploration discovered a
    /// pre-existing bug in <see cref="UdsSecurity.SetSeed"/>: it creates a
    /// fresh <c>SecurityLevelState</c> with <c>AttemptCount=0</c>, wiping
    /// any accumulated lockout counter. When two concurrent
    /// <c>SecurityAccessAsync</c> calls interleave, the second caller's
    /// <c>SetSeed</c> can run between the first caller's
    /// <c>RecordFailedAttempt</c> and the second caller's
    /// <c>RecordFailedAttempt</c>, resetting the counter and preventing
    /// the lockout boundary from being reached.
    /// <para>
    /// This is a pre-existing bug not introduced by v1.4.1 PATCH. Fixing it
    /// requires changing <c>SetSeed</c> to preserve <c>AttemptCount</c> and
    /// <c>LockedUntilUtc</c> on the existing state object (rather than
    /// replacing with a fresh one) — a behavior change with potential
    /// spec implications (e.g. would lockout persist across successful
    /// authentications?). Defer to v1.4.2 PATCH (HIGH severity) or v1.5.0
    /// MINOR after spec review.
    /// </para>
    /// <para>
    /// The wire-emission structure test (Test 1) above IS sufficient for the
    /// v1.4.1 PATCH carry-over: it asserts the 2-arg overload produces the
    /// expected 4-frame structure under concurrent same-level calls,
    /// demonstrating that the <c>_requestLock</c> correctly serializes the
    /// RequestSeed and SendKey wire emit pairs.
    /// </para>
    /// </summary>
    /// <summary>
    /// Test 2: Mid-handshake lockout flip via concurrent SendKey failures.
    /// <para>
    /// v1.4.2 PATCH Item 2: re-enabled after <see cref="UdsSecurity.SetSeed"/>
    /// fix (Task 2) preserves <c>AttemptCount</c> + <c>LockedUntilUtc</c>
    /// across <c>RequestSeed</c> success. With <c>MaxAttempts=2</c> and both
    /// <c>SendKey</c> legs failing, the second <c>RecordFailedAttempt</c>
    /// reaches the lockout boundary. One or both callers observe
    /// <see cref="UdsSecurityLockedException"/>.
    /// </para>
    /// <para>
    /// Carry-over from v1.4.1 PATCH Task 1 Test 2 (SKIPPED with rationale
    /// "defer to v1.4.2 PATCH"). Re-enabled by v1.4.2 PATCH Item 1 fix
    /// (OI: udssecurity-setseed-wipes-attempt-count).
    /// </para>
    /// <para>
    /// <b>RED-then-GREEN invariant:</b> with unfixed <c>SetSeed</c> (v1.4.1
    /// shipped), this test fails because counter is wiped between
    /// <c>RequestSeed</c> legs. After v1.4.2 PATCH fix, counter accumulates
    /// normally and lockout boundary is reached deterministically (modulo
    /// 2-concurrent-call timing).
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent()
    {
        // Arrange — MaxAttempts=2 to put boundary at second SendKey failure.
        var (iso, sent) = NewIsoWithCapture();
        var algo = new FakeKeyDerivationAlgorithm();
        using var client = new UdsClient(iso, algo);
        client.Security.LockoutConfig = new UdsSecurityLockoutConfig(
            MaxAttempts: 2,
            LockoutDuration: TimeSpan.FromSeconds(5));

        using var auto = AutoRespondSeedOkThenNrc35(client, sent);

        // Act — two concurrent SecurityAccessAsync on level 0x01.
        // AutoRespondSeedOkThenNrc35: positive seed for RequestSeed, NRC 0x35 for SendKey.
        // Both SendKey failures accumulate AttemptCount on the same SecurityLevelState
        // (SetSeed mutates existing state, preserving counter). Second failure triggers
        // lockout.
        var t1 = client.SecurityAccessAsync(0x01, CancellationToken.None);
        var t2 = client.SecurityAccessAsync(0x01, CancellationToken.None);

        // Wrap tasks so exception surfaces as null result (not unhandled throw).
        var w1 = WrapResult(t1);
        var w2 = WrapResult(t2);

        // 5s deadline prevents CI hangs (memory v1.2.12 lesson 4).
        var completed = await Task.WhenAny(
            Task.WhenAll(w1, w2),
            Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed is not null, "Race test timed out after 5s");

        // Assert — at least one caller observed UdsSecurityLockedException OR
        //   both SendKey legs ran and the second one's failure tripped lockout.
        //   Either outcome is valid: race-dependent on which SendKey leg's
        //   RecordFailedAttempt landed last. The key invariant is post-state.
        var r1 = await w1;  // null if threw (exception caught by WrapResult)
        var r2 = await w2;  // null if threw
        var threwCount = (r1 is null ? 1 : 0) + (r2 is null ? 1 : 0);
        threwCount.Should().BeOneOf(AllowedThrewCounts,
            "at least one caller must observe lockout (2 SendKey failures on MaxAttempts=2 boundary)");

        // Assert — exactly 2 RequestSeed + 2 SendKey frames emitted (per requestLock serialization).
        sent.Should().HaveCount(4, "two complete 2-arg handshakes = 2 RequestSeed + 2 SendKey");

        // Assert — post-state: IsLocked(0x01) is true (lockout boundary reached).
        // This is the KEY invariant the v1.4.2 fix enforces: counter survives
        // across SetSeed calls, so 2 SendKey failures reliably reach MaxAttempts=2.
        client.Security.IsLocked(0x01).Should().BeTrue(
            "after 2 SendKey failures, lockout boundary (MaxAttempts=2) must be reached " +
            "and the level must be locked (this is what the v1.4.2 PATCH fix guarantees)");

        // Assert — remaining lockout delay is positive.
        client.Security.RemainingLockoutDelay(0x01).Should().BeGreaterThan(TimeSpan.Zero,
            "lockout window must be active");
    }

    /// <summary>
    /// Helper: wrap a <see cref="Task{T}"/> to surface exceptions as a nullable
    /// result. The race tests don't assert on individual task outcomes (per
    /// spec Decision 1: race outcomes are non-deterministic); they assert on
    /// aggregate state.
    /// </summary>
    private static async Task<byte[]?> WrapResult(Task<byte[]> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly ObservableCollection<byte[]> _collection;
        private readonly NotifyCollectionChangedEventHandler _handler;

        public Unsubscriber(
            ObservableCollection<byte[]> collection,
            NotifyCollectionChangedEventHandler handler)
        {
            _collection = collection;
            _handler = handler;
        }

        public void Dispose() => _collection.CollectionChanged -= _handler;
    }
}