using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v1.2.11 PATCH (Item 1 + Item 2 prep): <see cref="TraceEntry.FrameType"/>
/// 必须区分 RTR 帧；<see cref="TraceEntry.Decoded"/> 改成 mutable 以便
/// DbcDecodeBackgroundService 异步填充。
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

    // --- v1.2.11 PATCH Item 2 prep: Decoded mutable + PropertyChanged ---

    [Fact]
    public void Decoded_Set_Fires_PropertyChanged()
    {
        // v1.2.11: DbcDecodeBackgroundService fills Decoded asynchronously,
        // so the property must be set-able AND fire PropertyChanged so the
        // WPF DataGrid row re-renders the Decoded column.
        var entry = new TraceEntry();
        var fired = new List<string?>();
        entry.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        entry.Decoded = "SigA=42";

        Assert.Equal("SigA=42", entry.Decoded);
        Assert.Contains(nameof(TraceEntry.Decoded), fired);
    }

    [Fact]
    public void Decoded_Set_Same_Value_Does_Not_Fire()
    {
        // v1.2.11: avoid spurious DataGrid re-renders when worker re-fills
        // with identical text (e.g. on a re-decode loop).
        var entry = new TraceEntry();
        var fired = 0;
        entry.PropertyChanged += (_, _) => fired++;

        entry.Decoded = "";

        Assert.Equal(0, fired);
    }
}