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
}
