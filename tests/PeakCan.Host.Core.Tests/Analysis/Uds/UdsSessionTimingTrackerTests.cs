using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

public sealed class UdsSessionTimingTrackerTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000), new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction SessionControl(byte sub, int p2, int p2Star)
    {
        var pdus = new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x10, sub),
            Pdu(UdsDirectionRole.Response, 0.010, 0x50, sub,
                (byte)(p2 >> 8), (byte)p2, (byte)(p2Star >> 8), (byte)p2Star),
        };
        return new UdsTransactionBuilder().Build(pdus).Transactions.Single();
    }

    private static UdsOfflineTransaction ReadDidTransaction()
    {
        var pdus = new[]
        {
            Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 1.020, 0x62, 0xF1, 0x90, 0x31),
        };
        return new UdsTransactionBuilder().Build(pdus).Transactions.Single();
    }

    private static UdsOfflineTransaction PendingOnlyTransaction()
    {
        var pdus = new[]
        {
            Pdu(UdsDirectionRole.Request, 2.0, 0x10, 0x03),
            Pdu(UdsDirectionRole.Response, 2.050, 0x7F, 0x10, 0x78),
        };
        return new UdsTransactionBuilder().Build(pdus).Transactions.Single();
    }

    [Fact]
    public void P2_and_P2star_are_read_in_correct_units()
    {
        var txs = new[] { SessionControl(0x02, p2: 0x0032, p2Star: 0x01F4) };
        var tracker = new UdsSessionTimingTracker();
        var timed = tracker.Apply(txs);

        tracker.Timing!.P2Ms.Should().Be(50);
        tracker.Timing.P2StarMs.Should().Be(5000);
        timed[0].TimingSource.Should().Be("ECU:0x50");
    }

    [Fact]
    public void No_session_control_leaves_timing_unknown()
    {
        var tracker = new UdsSessionTimingTracker();
        var timed = tracker.Apply(new[] { ReadDidTransaction() });
        tracker.Timing.Should().BeNull();
        timed[0].TimingSource.Should().Be("Unknown");
    }

    [Fact]
    public void Unclosed_transaction_has_no_total_duration_but_has_first_response()
    {
        var tracker = new UdsSessionTimingTracker();
        var timed = tracker.Apply(new[] { PendingOnlyTransaction() });
        timed[0].FirstResponseMs.Should().NotBeNull();
        timed[0].TotalMs.Should().BeNull();
    }

    [Fact]
    public void First_response_and_total_use_milliseconds()
    {
        var tracker = new UdsSessionTimingTracker();
        var timed = tracker.Apply(new[] { ReadDidTransaction() });

        timed[0].FirstResponseMs.Should().BeApproximately(20.0, 1e-6);
        timed[0].TotalMs.Should().BeApproximately(20.0, 1e-6);
    }
}
