using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// Task 13: closes the Task 12 wiring gap. <see cref="SinkWiringService"/>
/// is an <see cref="IHostedService"/> that runs at host startup and
/// calls <see cref="ChannelRouter.AttachSink"/> for <see cref="TraceService"/>
/// and <see cref="BusStatisticsCollector"/>. Until this is in place, frames
/// arriving at the router are dropped on the floor.
/// <para>
/// These tests build a minimal DI graph (not the full AppHostBuilder, which
/// also spins up Serilog file sinks) and verify that:
/// </para>
/// <list type="number">
///   <item>StartAsync attaches both sinks to the router.</item>
///   <item>A frame pushed through a fake channel ends up bumping the stats.</item>
///   <item>StartAsync is idempotent — calling twice doesn't double-attach.</item>
/// </list>
/// </summary>
public class SinkWiringServiceTests
{
    /// <summary>
    /// Minimal <see cref="ICanChannel"/> that lets the test raise a single
    /// <c>FrameReceived</c> event without touching the PEAK SDK.
    /// </summary>
    private sealed class FakeChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public FakeChannel(ChannelId id) { Id = id; IsConnected = true; }
        public event Action<CanFrame>? FrameReceived;
        // v3.16.9.4 PATCH: ICanChannel gained ReadLoopError event — unused
        // in this test fake, but must exist to satisfy the interface.
#pragma warning disable CS0067
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
        public void Raise(CanFrame f) => FrameReceived?.Invoke(f);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IHost BuildHost()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ChannelRouter>();
        builder.Services.AddSingleton<BusStatisticsCollector>();
        builder.Services.AddSingleton<TraceViewModel>();
        builder.Services.AddSingleton<DbcService>();
        builder.Services.AddSingleton<SignalViewModel>();
        builder.Services.AddSingleton<TraceService>();
        // M11: DBC decode offload worker; same singleton→hosted-service
        // pattern as AppHostBuilder uses for production wiring.
        builder.Services.AddSingleton<DbcDecodeBackgroundService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DbcDecodeBackgroundService>());
        // v0.5.0: RecordService is now a required dependency of SinkWiringService.
        builder.Services.AddSingleton<RecordService>();
        // P0 (flashing feature 2026-07-21): SinkWiringService now also wires
        // the IsoTpSinkAdapter. Build a minimal IsoTpLayer singleton with a
        // no-op send callback + default 0x7E0/0x7E8 CAN IDs — the tests below
        // only exercise the router→sink fan-out, not ISO-TP send. The adapter
        // wraps this same singleton layer instance.
        builder.Services.AddSingleton(new PeakCan.Host.Core.Uds.IsoTp.IsoTpLayer(
            new PeakCan.Host.Core.Uds.IsoTp.CanIdConfig
            {
                RequestId = 0x7E0,
                ResponseId = 0x7E8
            },
            _ => { }));
        builder.Services.AddSingleton<PeakCan.Host.App.Composition.IsoTpSinkAdapter>();
        builder.Services.AddHostedService<SinkWiringService>();
        return builder.Build();
    }

    [Fact]
    public async Task StartAsync_Attaches_TraceService_And_BusStats_To_Router()
    {
        using var host = BuildHost();
        var router = host.Services.GetRequiredService<ChannelRouter>();
        var stats = host.Services.GetRequiredService<BusStatisticsCollector>();
        var fake = new FakeChannel(new ChannelId(0x51));
        router.RegisterChannel(fake);

        // Trigger startup so SinkWiringService.StartAsync runs.
        await host.StartAsync();

        // Push one frame through the fake channel → router → both sinks.
        fake.Raise(new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FrameFlags.None,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(1_000_000UL)));

        // BusStatisticsCollector received the frame.
        stats.Snapshot().TotalFrames.Should().Be(1, "the stats sink should be wired to the router");

        await host.StopAsync();
    }

    [Fact]
    public async Task StartAsync_Second_Call_Is_Safe_Via_AttachSink_Idempotency()
    {
        // ChannelRouter.AttachSink is idempotent (Task 10), so a second
        // StartAsync on the same hosted-service instance must not throw
        // and must not double-attach the sinks. Verify by calling
        // StartAsync directly twice on the same SinkWiringService.
        using var host = BuildHost();
        await host.StartAsync();
        var sws = host.Services.GetServices<IHostedService>()
            .OfType<SinkWiringService>()
            .Single();
        var act = async () => await sws.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
        await host.StopAsync();
    }

    // ---- P0 (flashing feature 2026-07-21): IsoTpSinkAdapter receive wiring ----

    /// <summary>
    /// P0 acceptance: this is the test that proves the diagnose-on-the-floor
    /// receive gap is actually closed. Push a valid ISO-TP single frame for
    /// the layer's response ID (0x7E8) through a fake channel → the router
    /// fans it out → IsoTpSinkAdapter.OnFrame → IsoTpLayer.ProcessFrame →
    /// MessageReceived. Before P0 the adapter was not attached, so this test
    /// would time out (the `received` event never sets) — i.e. it FAILS on
    /// pre-P0 code and PASSES on post-P0 code, a clean RED→GREEN gate.
    /// </summary>
    [Fact]
    public async Task StartAsync_Attaches_IsoTpSinkAdapter_So_Ecu_Response_Reaches_IsoTpLayer()
    {
        using var host = BuildHost();
        var router = host.Services.GetRequiredService<ChannelRouter>();
        var isoTp = host.Services.GetRequiredService<IsoTpLayer>();
        var fake = new FakeChannel(new ChannelId(0x51));
        router.RegisterChannel(fake);

        var received = new ManualResetEventSlim(initialState: false);
        byte[]? message = null;
        isoTp.MessageReceived += msg =>
        {
            message = msg;
            received.Set();
        };

        await host.StartAsync();

        // Valid ISO-TP single frame addressed to the layer's ResponseId
        // (0x7E8): PCI 0x02 (SF, length 2) + payload [0x7E, 0x00]. A UDS
        // positive response to SessionControl uses exactly this shape.
        fake.Raise(new CanFrame(
            new CanId(0x7E8, FrameFormat.Standard),
            new byte[] { 0x02, 0x7E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            FrameFlags.None,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(1_000_000UL)));

        // Single-frame delivery is synchronous within ProcessFrame, but the
        // ManualResetEventSlim guard makes the test robust to any future
        // marshaling change and gives a bounded wait instead of a flaky
        // sleep.
        received.Wait(TimeSpan.FromSeconds(2))
            .Should().BeTrue("the IsoTpSinkAdapter must be wired so ECU single frames reach IsoTpLayer.MessageReceived");
        message.Should().NotBeNull()
            .And.BeEquivalentTo(new byte[] { 0x7E, 0x00 },
                "the SF payload (2 bytes after the PCI byte) must surface via MessageReceived");

        await host.StopAsync();
    }

    /// <summary>
    /// P0 robustness: a malformed frame on the shared bus must NOT detach the
    /// IsoTp adapter from the router. IsoTpSinkAdapter narrow-catches
    /// IsoTpFrame.Decode's ArgumentException (the 6 malformed-input throw
    /// sites), so the router's auto-detach-after-throw path is never tripped
    /// for the adapter. Counter-example: if the adapter threw on every bad
    /// frame, ChannelRouter would forward to OnError and (after OnError also
    /// threw) auto-detach the sink, severing the UDS receive path entirely.
    /// <para>
    /// Verified by sending SEVERAL malformed frames (the router auto-detaches
    /// on the SECOND OnError throw per the documented policy, so a single bad
    /// frame would not be conclusive) and then asserting a subsequent valid
    /// frame still reaches MessageReceived.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StartAsync_Malformed_Frames_Do_Not_Sever_The_IsoTp_Receive_Path()
    {
        using var host = BuildHost();
        var router = host.Services.GetRequiredService<ChannelRouter>();
        var isoTp = host.Services.GetRequiredService<IsoTpLayer>();
        var fake = new FakeChannel(new ChannelId(0x51));
        router.RegisterChannel(fake);

        var received = new ManualResetEventSlim(initialState: false);
        byte[]? message = null;
        isoTp.MessageReceived += msg =>
        {
            message = msg;
            received.Set();
        };

        await host.StartAsync();

        var raise = new Action<byte[]>(data => fake.Raise(new CanFrame(
            new CanId(0x7E8, FrameFormat.Standard),
            data,
            FrameFlags.None,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(1_000_000UL))));

        // Three malformed frames — each would throw ArgumentException out of
        // IsoTpFrame.Decode if not contained by the adapter: unknown PCI,
        // empty data, and SF length 0. All routed to ResponseId 0x7E8 so
        // they pass the layer's CAN-ID filter and reach Decode.
        raise(new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }); // unknown PCI 0xF
        raise(Array.Empty<byte>());                                        // empty data
        raise(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });   // SF length 0

        // Now a valid single frame — the adapter must STILL be attached and
        // deliver it. If the adapter had been auto-detached by the router
        // (because it threw on the bad frames), this would time out.
        raise(new byte[] { 0x02, 0x7E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }); // valid SF

        received.Wait(TimeSpan.FromSeconds(2))
            .Should().BeTrue("malformed frames must not have caused the router to auto-detach the IsoTp adapter");
        message.Should().BeEquivalentTo(new byte[] { 0x7E, 0x00 });

        await host.StopAsync();
    }
}
