using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.Cmac;
using PeakCan.Security.SecOc;

namespace PeakCan.Host.Infrastructure.Tests.Channel.SecOc;

/// <summary>
/// SecOcChannel decorator tests (spec §5-D1 / §5-D6, Phase 2 DoD assertions).
/// </summary>
public class SecOcChannelTests
{
    private const uint TestCanId = 0x123;
    private const uint OtherCanId = 0x456;
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff");

    private static SecOcProfile Profile() => new() { DataId = 0x0001, FvLenBits = 16, MacLenBits = 24 };

    private static SecOcChannelOptions Options(SecOcPduMode mode = SecOcPduMode.Both,
        uint canId = TestCanId, uint initialFv = 0)
        => new()
        {
            ProtectedPdus = new Dictionary<uint, SecOcPduConfig>
            {
                [canId] = new() { Profile = Profile(), Key = Key, Mode = mode, InitialFv = initialFv },
            },
        };

    private static SecOcChannel Create(out VirtualChannel inner,
        SecOcPduMode mode = SecOcPduMode.Both,
        SecOcVerdictTable? verdicts = null, SecOcStats? stats = null)
    {
        inner = new VirtualChannel();
        return new SecOcChannel(inner, Options(mode), verdicts, stats);
    }

    private static CanFrame Frame(uint canId, byte[] data)
        => new(new CanId(canId, FrameFormat.Standard), data, FrameFlags.None, ChannelId.None, new Timestamp(0));

    /// <summary>Builds a correctly secured frame (data‖TruncFV‖TruncMAC) with fv sequence 0,1,2…</summary>
    private static byte[] SecuredPayload(byte[] authenticData, int fvStep = 0)
    {
        var auth = new SecOcAuthenticator(Profile(), Key, new BouncyCastleCmacProvider());
        for (var i = 0; i < fvStep; i++) auth.Sign(authenticData, new byte[64]);
        var frame = new byte[64];
        var len = auth.Sign(authenticData, frame);
        return frame[..len];
    }

    // ---- composition guard (DoD ⑤: single layer) ----

    [Fact]
    public void Ctor_RejectsDoubleWrapping()
    {
        var inner = new VirtualChannel();
        var first = new SecOcChannel(inner, Options());

        var act = () => new SecOcChannel(first, Options());
        act.Should().Throw<InvalidOperationException>().WithMessage("*exactly once*");
    }

    [Fact]
    public void Ctor_RejectsWrongKeyLength()
    {
        var inner = new VirtualChannel();
        var options = new SecOcChannelOptions
        {
            ProtectedPdus = new Dictionary<uint, SecOcPduConfig>
            {
                [TestCanId] = new() { Profile = Profile(), Key = new byte[15] },
            },
        };
        var act = () => new SecOcChannel(inner, options);
        act.Should().Throw<ArgumentException>();
    }

    // ---- TX direction ----

    [Fact]
    public async Task Tx_Both_SignsFrame_AppendsFvAndMacTrailer()
    {
        var channel = Create(out var inner);
        var onBus = new List<CanFrame>();
        inner.FrameReceived += f => onBus.Add(f);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        await channel.WriteAsync(Frame(TestCanId, [1, 2, 3, 4]));
        await Task.Delay(100);

        var secured = Assert.Single(onBus);
        // payload 4 + fvLen 2 + macLen 3 = 9
        secured.Data.Length.Should().Be(9);
        secured.Data.Span[..4].ToArray().Should().Equal([1, 2, 3, 4]);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Tx_VerifyMode_PassesThroughUnsigned()
    {
        var channel = Create(out var inner, SecOcPduMode.Verify);
        var onBus = new List<CanFrame>();
        inner.FrameReceived += f => onBus.Add(f);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        await channel.WriteAsync(Frame(TestCanId, [1, 2, 3, 4]));
        await Task.Delay(100);

        Assert.Single(onBus);
        onBus[0].Data.Length.Should().Be(4);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Tx_UnprotectedCanId_PassesThrough()
    {
        var channel = Create(out var inner);
        var onBus = new List<CanFrame>();
        inner.FrameReceived += f => onBus.Add(f);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        await channel.WriteAsync(Frame(OtherCanId, [1, 2, 3, 4]));
        await Task.Delay(100);

        Assert.Single(onBus);
        onBus[0].Data.Length.Should().Be(4);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Tx_BypassMode_PassesThroughUnsigned()
    {
        var channel = Create(out var inner, SecOcPduMode.Bypass);
        var onBus = new List<CanFrame>();
        inner.FrameReceived += f => onBus.Add(f);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        await channel.WriteAsync(Frame(TestCanId, [1, 2, 3, 4]));
        await Task.Delay(100);

        Assert.Single(onBus);
        onBus[0].Data.Length.Should().Be(4);
        await channel.DisposeAsync();
    }

    // ---- RX direction (frames injected on the bus side of the decorator) ----

    [Fact]
    public async Task Rx_ValidFrame_Accepts_Forwards_And_RecordsVerdict()
    {
        var verdicts = new SecOcVerdictTable();
        var channel = Create(out var inner, SecOcPduMode.Verify, verdicts);
        var forwarded = new List<CanFrame>();
        channel.FrameReceived += f => forwarded.Add(f);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        await inner.WriteAsync(Frame(TestCanId, SecuredPayload([1, 2, 3, 4])));
        await Task.Delay(100);

        Assert.Single(forwarded);
        verdicts.TryGet(inner.Id.Handle, 1, out var verdict).Should().BeTrue();
        verdict.Accepted.Should().BeTrue();
        verdict.Reason.Should().BeNull();
        await channel.DisposeAsync();
    }

    // DoD ②: corrupted FV region must classify as BadMac (MAC covers FV),
    // not FvRollback — see spec §6.2 attack mapping table.
    [Fact]
    public async Task Rx_ForgedFv_ClassifiesAsBadMac()
    {
        var verdicts = new SecOcVerdictTable();
        var stats = new SecOcStats();
        var channel = Create(out var inner, SecOcPduMode.Verify, verdicts, stats);
        channel.FrameReceived += _ => { };
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var frame = SecuredPayload([1, 2, 3, 4]); // 9 bytes: data 4 + fv 2 + mac 3
        frame[4] ^= 0xFF;                          // FV region
        await inner.WriteAsync(Frame(TestCanId, frame));
        await Task.Delay(100);

        verdicts.TryGet(inner.Id.Handle, 1, out var verdict).Should().BeTrue();
        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be(RejectReason.BadMac);
        await channel.DisposeAsync();
    }

    // DoD ①: TX BadMac — corrupt the MAC region AFTER signing (fault injector
    // sits inside SecOcChannel), RX side must reject with BadMac.
    [Fact]
    public async Task Loopback_TxCorruptMac_RxRejectsBadMac()
    {
        var txInner = new VirtualChannel();
        var txFaults = new FaultInjector(txInner);
        var tx = new SecOcChannel(txFaults, Options(SecOcPduMode.Sign));

        var verdicts = new SecOcVerdictTable();
        var rxInner = new VirtualChannel();
        var rx = new SecOcChannel(rxInner, Options(SecOcPduMode.Verify), verdicts);
        var received = new List<CanFrame>();
        rx.FrameReceived += f => received.Add(f);

        await tx.ConnectAsync(BaudRate.Can500kbps, false);
        await rx.ConnectAsync(BaudRate.Can500kbps, false);

        // Corrupt one MAC byte on the wire (MAC region = last 3 bytes of 9).
        txFaults.AddFault(new FaultRule
        {
            Type = FaultType.Corrupt,
            TargetCanId = TestCanId,
            CorruptByteIndices = [8],
            CorruptXorMask = 0xFF,
        });

        // Bridge: whatever tx inner emits goes onto the rx bus.
        txInner.FrameReceived += f => _ = rxInner.WriteAsync(f).AsTask();

        await tx.WriteAsync(Frame(TestCanId, [1, 2, 3, 4]));
        await Task.Delay(200);

        Assert.Single(received);
        verdicts.TryGet(rxInner.Id.Handle, 1, out var verdict).Should().BeTrue();
        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be(RejectReason.BadMac);
        await tx.DisposeAsync();
        await rx.DisposeAsync();
    }

    // DoD ③: re-injecting an OLDER valid frame (within window) → FvRollback.
    [Fact]
    public async Task Rx_ReplayedOlderFrame_ClassifiesAsFvRollback()
    {
        var verdicts = new SecOcVerdictTable();
        var channel = Create(out var inner, SecOcPduMode.Verify, verdicts);
        channel.FrameReceived += _ => { };
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var fv0 = SecuredPayload([1, 2, 3, 4], fvStep: 0);
        var fv1 = SecuredPayload([1, 2, 3, 4], fvStep: 1);
        var fv2 = SecuredPayload([1, 2, 3, 4], fvStep: 2);

        await inner.WriteAsync(Frame(TestCanId, fv0));
        await inner.WriteAsync(Frame(TestCanId, fv1));
        await inner.WriteAsync(Frame(TestCanId, fv2));
        await inner.WriteAsync(Frame(TestCanId, fv1)); // older, diff=1 ≤ window
        await Task.Delay(200);

        verdicts.TryGet(inner.Id.Handle, 4, out var verdict).Should().BeTrue();
        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be(RejectReason.FvRollback);
        await channel.DisposeAsync();
    }

    // DoD ④: re-sending the SAME frame → Replay.
    [Fact]
    public async Task Rx_ReplayedSameFrame_ClassifiesAsReplay()
    {
        var verdicts = new SecOcVerdictTable();
        var channel = Create(out var inner, SecOcPduMode.Verify, verdicts);
        channel.FrameReceived += _ => { };
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var frame = SecuredPayload([1, 2, 3, 4]);
        await inner.WriteAsync(Frame(TestCanId, frame));
        await inner.WriteAsync(Frame(TestCanId, frame));
        await Task.Delay(200);

        verdicts.TryGet(inner.Id.Handle, 2, out var verdict).Should().BeTrue();
        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be(RejectReason.Replay);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Rx_RejectedFrame_IsStillForwarded()
    {
        var channel = Create(out var inner, SecOcPduMode.Verify);
        var forwarded = new List<CanFrame>();
        channel.FrameReceived += f => forwarded.Add(f);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var frame = SecuredPayload([1, 2, 3, 4]);
        frame[8] ^= 0xFF;
        await inner.WriteAsync(Frame(TestCanId, frame));
        await Task.Delay(100);

        // Test host forwards rejected frames so the trace can badge them (spec D6.7);
        // the verdict table carries the rejection.
        Assert.Single(forwarded);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Rx_UnprotectedCanId_NoVerdictRecorded()
    {
        var verdicts = new SecOcVerdictTable();
        var channel = Create(out var inner, SecOcPduMode.Both, verdicts);
        channel.FrameReceived += _ => { };
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        await inner.WriteAsync(Frame(OtherCanId, [1, 2, 3]));
        await Task.Delay(100);

        verdicts.Count.Should().Be(0);
        await channel.DisposeAsync();
    }

    // ---- stats (spec D6.2) ----

    [Fact]
    public async Task Stats_AccumulatesPerCanIdBuckets()
    {
        var stats = new SecOcStats();
        var channel = Create(out var inner, SecOcPduMode.Verify, stats: stats);
        channel.FrameReceived += _ => { };
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        await inner.WriteAsync(Frame(TestCanId, SecuredPayload([1, 2, 3, 4])));

        var bad = SecuredPayload([1, 2, 3, 4]);
        bad[8] ^= 0xFF;
        await inner.WriteAsync(Frame(TestCanId, bad));
        await Task.Delay(200);

        stats.TryGet(TestCanId, out var bucket).Should().BeTrue();
        bucket.Accepted.Should().Be(1);
        bucket.Rejected.Should().Be(1);
        bucket.LastReason.Should().Be(RejectReason.BadMac);
        stats.TotalAccepted.Should().Be(1);
        stats.TotalRejected.Should().Be(1);
        await channel.DisposeAsync();
    }
}
