using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

public class AscDirectionTests
{
    [Fact]
    public void TryParseDataLine_TxMarker_SetsIsTransmitTrue()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  18FFFAF4x       Tx   d 8  F4 00 00 00 00 00 00 00",
            out var frame, out var reason);

        Assert.True(ok, reason);
        Assert.True(frame.IsTransmit);
    }

    [Fact]
    public void TryParseDataLine_RxMarker_SetsIsTransmitFalse()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  18FFFAF4x       Rx   d 8  F4 00 00 00 00 00 00 00",
            out var frame, out _);

        Assert.True(ok);
        Assert.False(frame.IsTransmit);
    }

    [Fact]
    public void TryParseDataLine_NoDirectionMarker_LeavesNull()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  18FFFAF4x       d 8  F4 00 00 00 00 00 00 00",
            out var frame, out _);

        Assert.True(ok);
        Assert.Null(frame.IsTransmit);
    }

    [Fact]
    public void TryParseDataLine_RtrMarker_SetsRtrFlag()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  123           Rx   rtr",
            out var frame, out _);

        Assert.True(ok);
        Assert.True((frame.Flags & PeakCan.HIL.Core.FrameFlags.Rtr) != 0);
    }
}
