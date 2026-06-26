using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v1.2.11 PATCH (Item 1): <see cref="TraceEntry.FrameType"/> 必须区分 RTR
/// 帧，与 ERR / FD 并列。优先级: ERR > RTR > FD > ""。
/// </summary>
public class TraceEntryTests
{
    [Fact]
    public void FrameType_Rtr_Only_Returns_Rtr()
    {
        var entry = new TraceEntry { IsRtr = true, IsFd = false, IsError = false };
        Assert.Equal("RTR", entry.FrameType);
    }

    [Fact]
    public void FrameType_Error_Takes_Precedence_Over_Rtr()
    {
        var entry = new TraceEntry { IsRtr = true, IsFd = false, IsError = true };
        Assert.Equal("ERR", entry.FrameType);
    }

    [Fact]
    public void FrameType_Rtr_With_Fd_Still_Returns_Rtr()
    {
        var entry = new TraceEntry { IsRtr = true, IsFd = true, IsError = false };
        Assert.Equal("RTR", entry.FrameType);
    }

    [Fact]
    public void FrameType_Standard_Returns_Empty()
    {
        var entry = new TraceEntry { IsRtr = false, IsFd = false, IsError = false };
        Assert.Equal("", entry.FrameType);
    }

    [Fact]
    public void FrameType_Fd_Returns_Fd()
    {
        var entry = new TraceEntry { IsRtr = false, IsFd = true, IsError = false };
        Assert.Equal("FD", entry.FrameType);
    }
}