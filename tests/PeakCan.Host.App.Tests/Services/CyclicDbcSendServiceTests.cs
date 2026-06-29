using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v1.5.1 PATCH Item 2 (Periodic DBC send): verifies
/// <see cref="CyclicDbcSendService"/> start/stop lifecycle and per-tick
/// re-encoding of <see cref="Message"/> + signal values via
/// <see cref="DbcEncodeService"/> + dispatch through
/// <see cref="SendService"/>. Mirrors <c>CyclicSendServiceTests.cs</c> but
/// exercises the DBC encode path independently of the cyclic raw-frame
/// service (per Decision 7).
/// </summary>
public class CyclicDbcSendServiceTests
{
    /// <summary>
    /// Hand-rolled <see cref="SendService"/> subclass that captures every
    /// <see cref="SendAsync"/> invocation. Same pattern as
    /// <c>CountingSendService</c> in <c>CyclicSendServiceRaceTests.cs</c> +
    /// <c>FakeSendService</c> in <c>DbcSendViewModelTests.cs</c>.
    /// </summary>
    private sealed class CountingDbcSendService : SendService
    {
        public CountingDbcSendService() : base(NullLogger<SendService>.Instance) { }
        public List<CanFrame> Sent { get; } = new();
        public int CallCount => Sent.Count;

        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        {
            Sent.Add(frame);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }
    }

    private static Signal SignalUnsigned(string name) =>
        new(name, 0, 8, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 100, "", Array.Empty<string>());

    private static Message MakeMessage(uint id = 0x100, string name = "TestMsg", byte dlc = 8, params Signal[] signals) =>
        new(id, name, dlc, "Test",
            signals,
            IsMultiplexed: false,
            MultiplexorSignalIndex: null);

    [Fact]
    public void IsRunning_False_By_Default()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        svc.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Start_Sets_IsRunning_True()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg = MakeMessage(signals: SignalUnsigned("S"));

        svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 1 }), TimeSpan.FromMilliseconds(100));
        try
        {
            svc.IsRunning.Should().BeTrue();
        }
        finally
        {
            svc.Stop();
        }
    }

    [Fact]
    public void Stop_Sets_IsRunning_False()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg = MakeMessage(signals: SignalUnsigned("S"));

        svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 1 }), TimeSpan.FromMilliseconds(100));
        svc.Stop();
        svc.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Stop_Is_Idempotent()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);

        svc.Stop();
        svc.Stop();
        // must not throw
    }

    [Fact]
    public async Task Start_EncodesDbcMessage_Periodically()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg = MakeMessage(signals: SignalUnsigned("S"));

        svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 0x42 }),
                  TimeSpan.FromMilliseconds(50));
        // Wait up to 2 s for at least one tick (CI Windows runners can be slow).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (send.CallCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        svc.Stop();

        send.CallCount.Should().BeGreaterThan(0, "cyclic DBC send should have fired at least once");
        svc.SuccessCount.Should().BeGreaterThan(0);
        svc.FailureCount.Should().Be(0);

        // Verify the encoded frame has the expected ID + payload
        var frame = send.Sent[0];
        frame.Id.Raw.Should().Be(0x100u);
        frame.Data.ToArray()[0].Should().Be(0x42);
    }

    [Fact]
    public async Task Start_EncodesWithUpdatedSignalValues_OnEachTick()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg = MakeMessage(signals: SignalUnsigned("S"));
        int counter = 0;

        svc.Start(() =>
        {
            counter++;
            return (msg, new Dictionary<string, double> { ["S"] = (double)(counter & 0xFF) });
        }, TimeSpan.FromMilliseconds(30));

        // Wait long enough to capture at least 2 distinct payload values.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (send.CallCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        svc.Stop();

        send.CallCount.Should().BeGreaterThanOrEqualTo(2);
        // At least two distinct payload values
        var distinctPayloads = send.Sent.Select(f => f.Data.ToArray()[0]).Distinct().ToList();
        distinctPayloads.Count.Should().BeGreaterThan(1,
            "value-provider should be re-invoked each tick so updates flow into encoded frames");
    }

    [Fact]
    public void Start_Stops_Previous_Cyclic_DbcSend()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg1 = MakeMessage(id: 0x100, signals: SignalUnsigned("S"));
        var msg2 = MakeMessage(id: 0x200, signals: SignalUnsigned("S"));

        svc.Start(() => (msg1, new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(100));
        svc.Start(() => (msg2, new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(100));
        svc.IsRunning.Should().BeTrue();
        svc.Stop();
    }

    [Fact]
    public async Task Start_EncodeFailure_IncrementsFailureCount()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg = MakeMessage(signals: SignalUnsigned("S"));

        // Value 200.0 is outside the [0, 100] range defined by SignalUnsigned
        // → DbcEncodeService.Encode throws DbcSignalValueOutOfRangeException
        svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 200.0 }),
                  TimeSpan.FromMilliseconds(30));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (svc.FailureCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        svc.Stop();

        svc.FailureCount.Should().BeGreaterThan(0);
        svc.SuccessCount.Should().Be(0);
        send.CallCount.Should().Be(0, "encode failure must NOT reach SendService.SendAsync");
    }

    [Fact]
    public async Task Start_MessageChangedMidRun_StopsService()
    {
        var send = new CountingDbcSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg1 = MakeMessage(id: 0x100, signals: SignalUnsigned("S"));
        var msg2 = MakeMessage(id: 0x200, signals: SignalUnsigned("S"));
        int tick = 0;

        svc.Start(() =>
        {
            // Return msg1 first, then switch to msg2 to simulate user
            // changing the message dropdown while periodic send is active.
            tick++;
            return (tick <= 2 ? msg1 : msg2, new Dictionary<string, double> { ["S"] = 1 });
        }, TimeSpan.FromMilliseconds(40));

        // Wait until both ticks occurred and service stopped itself
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (svc.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        svc.IsRunning.Should().BeFalse(
            "message change mid-run should auto-stop the cyclic DBC send");

        // FailureCount should have at least one tick that observed the id mismatch.
        // (It may also have encode failures if the new message has different signals,
        //  but the key invariant is auto-stop + non-zero FailureCount increment.)
        svc.FailureCount.Should().BeGreaterThan(0);
    }
}
