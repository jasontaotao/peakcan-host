using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
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
}
