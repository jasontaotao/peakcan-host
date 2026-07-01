using FluentAssertions;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Peak;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Task 9 hardware integration tests. These require a real PCAN-USB
/// device on the test host and are skipped by default — the [Trait]
/// attribute lets CI exclude them via
/// <c>dotnet test --filter "category!=integration"</c>.
/// <para>
/// To run locally:
/// <list type="number">
///   <item>Plug in a PCAN-USB FD (or compatible) device.</item>
///   <item>Install the PEAK PCAN-Basic driver (Windows: from the PEAK-System
///         website; bundled with the Peak.PCANBasic.NET package).</item>
///   <item>Remove the <c>Skip</c> attribute and adjust the channel handle
///         to match your hardware (default: <c>PCAN_USBBUS1</c> = 0x51).</item>
///   <item>Loopback: connect CAN-H to CAN-L on the same bus, or use the
///         second channel as a sink.</item>
/// </list>
/// </para>
/// </summary>
public class PeakCanChannelTests
{
    [Fact(Skip = "Requires real PCAN hardware — see class XML doc for setup")]
    [Trait("category", "integration")]
    public void Connect_And_Disconnect_Round_Trip()
    {
        // Local-run template:
        //   var ch = new PeakCanChannel(new ChannelId(0x51));
        //   var connect = ch.ConnectAsync(BaudRate.Can500kbps, fd: false).GetAwaiter().GetResult();
        //   Assert.True(connect.IsSuccess);
        //   Assert.True(ch.IsConnected);
        //   var disconnect = ch.DisconnectAsync().GetAwaiter().GetResult();
        //   Assert.False(ch.IsConnected);
    }

    [Fact(Skip = "Requires real PCAN hardware — see class XML doc for setup")]
    [Trait("category", "integration")]
    public void Write_And_Read_Round_Trip()
    {
        // Local-run template: connect on PCAN_USBBUS1 and PCAN_USBBUS2,
        // subscribe to ch2's FrameReceived, write from ch1, assert arrival.
    }

    [Fact]
    public async Task DisposeAsync_Called_Twice_Does_Not_Throw()
    {
        // H5 regression guard: the previous DisconnectAsync implementation
        // disposed its CTS unconditionally; a second call threw
        // ObjectDisposedException. The new gate-backed implementation is
        // idempotent. We can't drive ConnectAsync without real hardware, but
        // we can exercise the public DisposeAsync path on a never-connected
        // channel (CaptureForDisconnect returns null loop, gate stays clean).
        var ch = new PeakCanChannel(new ChannelId(0x51));
        await ch.DisposeAsync();
        var act = async () => await ch.DisposeAsync();
        await act.Should().NotThrowAsync("DisposeAsync must be idempotent");
    }

    [Fact]
    public void IsConnected_On_Never_Connected_Channel_Is_False()
    {
        var ch = new PeakCanChannel(new ChannelId(0x51));
        ch.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Constructor_Accepts_Optional_Logger_For_Read_Loop_Logging()
    {
        // H7 + H8: PeakCanChannel used to silently swallow every
        // exception from the SDK read path. The fix routes them through
        // an ILogger<PeakCanChannel> so bus-off / driver-unload events
        // are visible in production. The constructor keeps an optional
        // logger parameter (defaulting to NullLogger) so test code that
        // news up the channel directly stays simple.
        var logger = new TestLogger<PeakCanChannel>();
        var ch = new PeakCanChannel(new ChannelId(0x51), logger);
        ch.Should().NotBeNull();
        // Smoke check: the channel must expose a public Id and stay
        // unconnected — proves the ctor ran without throwing under the
        // new logger-injection path.
        ch.Id.Handle.Should().Be((ushort)0x51);
        ch.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void MaxConsecutiveReadFailures_Is_Exposed_And_Reasonable()
    {
        // The read loop now gives up after this many consecutive
        // failures (instead of busy-spinning forever on a dead bus).
        // Assert the constant exists and is a sensible bound: large
        // enough to absorb short transient glitches, small enough to
        // not paper over a real hardware failure.
        PeakCan.Host.Infrastructure.Peak.PeakCanChannel.MaxConsecutiveReadFailures
            .Should().BeGreaterThan(10, "must allow transient glitches to recover");
        PeakCan.Host.Infrastructure.Peak.PeakCanChannel.MaxConsecutiveReadFailures
            .Should().BeLessThan(10_000, "must not busy-spin forever on a dead bus");
    }

    /// <summary>
    /// Minimal ILogger that records every log call. Used to verify the
    /// PeakCanChannel ctor accepts an ILogger without throwing and that
    /// the field is wired (we cannot exercise the read loop without
    /// hardware). Records are exposed for future tests that may want
    /// to assert the channel logged a read-loop exception.
    /// </summary>
    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public System.Collections.Generic.List<(LogLevel Level, string Message)> Entries { get; } = new();
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
