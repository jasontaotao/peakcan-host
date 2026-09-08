using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.HIL.Core;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;
using PeakCan.Host.Mobile.Core.Tests.Fakes;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class TraceChartViewModelTests
{
    private const string Dbc = """
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
         SG_ EngineTemp : 16|8@1+ (1,-40) [0|215] "C" Vector__XXX

        BO_ 300 MuxData: 8 ECM
         SG_ MuxSelector M : 0|8@1+ (1,0) [0|1] "" Vector__XXX
         SG_ LowValue m0 : 8|8@1+ (1,0) [0|255] "" Vector__XXX
         SG_ HighValue m1 : 8|8@1+ (1,0) [0|255] "" Vector__XXX
        """;

    private static SignalSelectionKey Speed { get; } = new(0x100, false, "EngineData", "EngineSpeed");
    private static SignalSelectionKey Temp { get; } = new(0x100, false, "EngineData", "EngineTemp");
    private static SignalSelectionKey High { get; } = new(0x12C, false, "MuxData", "HighValue");

    private static TraceChartViewModel Create(string text = Dbc)
    {
        var catalog = DbcCatalog.Parse(text).Catalog!;
        return new TraceChartViewModel(catalog, new FakeUiDispatcher());
    }

    [Fact]
    public void Constructor_Exposes_Dbc_Messages()
    {
        var vm = Create();

        vm.Messages.Should().HaveCount(2);
        vm.SelectedSignals.Should().BeEmpty();
        vm.Cursor.Should().BeNull();
    }

    [Fact]
    public void Select_Accepts_At_Most_Two_Signals()
    {
        var vm = Create();
        var unknown = new SignalSelectionKey(0x101, false, "EngineData", "EngineSpeed");

        vm.Select(unknown).Should().BeFalse();
        vm.Select(Speed).Should().BeTrue();
        vm.Select(Temp).Should().BeTrue();
        vm.Select(High).Should().BeFalse();

        vm.SelectedSignals.Should().HaveCount(2);
    }

    [Fact]
    public void Deselect_Removes_Selected_Signal()
    {
        var vm = Create();
        vm.Select(Speed);
        vm.Select(Temp);

        vm.Deselect(Temp);

        vm.SelectedSignals.Should().ContainSingle(i => i.Key == Speed);
    }

    [Fact]
    public void Ingest_Decodes_Only_Selected_Signal_And_Can_Id()
    {
        var vm = Create();
        vm.Select(Speed);

        vm.Ingest(new ReplayFrame(1, 0x100, 8, [0x01, 0x02], FrameFlags.None));
        vm.Ingest(new ReplayFrame(2, 0x12C, 8, [0x01, 0x42], FrameFlags.None));
        vm.RefreshRender();

        vm.RenderPoints.Should().HaveCount(1);
        vm.RenderPoints[Speed].Should().HaveCount(1);
        vm.RenderPoints[Speed].Max(p => p.Value).Should().Be(128.25);
    }

    [Fact]
    public void Ingest_Respects_Multiplexor()
    {
        var vm = Create();
        vm.Select(High);

        vm.Ingest(new ReplayFrame(1, 0x12C, 8, [0x01, 0x42], FrameFlags.None));
        vm.Ingest(new ReplayFrame(2, 0x12C, 8, [0x00, 0x42], FrameFlags.None));
        vm.RefreshRender();

        vm.RenderPoints[High].Should().Contain(p => p.Value == 66);
    }

    [Fact]
    public void SetCatalog_Clears_Samples_And_Preserves_Valid_Selection()
    {
        var vm = Create();
        vm.Select(Speed);
        vm.Ingest(new ReplayFrame(1, 0x100, 8, [0x01, 0x02], FrameFlags.None));

        vm.SetCatalog(DbcCatalog.Parse(Dbc).Catalog);

        vm.SelectedSignals.Should().ContainSingle(i => i.Key == Speed);
        vm.RenderPoints.Should().BeEmpty();
    }

    [Fact]
    public void SetCatalog_Removes_Invalid_Selection()
    {
        var vm = Create();
        vm.Select(Speed);

        const string otherDbc = """
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 OtherData: 8 ECM
             SG_ OtherSignal : 0|16@1+ (1,0) [0|0] "" Vector__XXX
            """;
        vm.SetCatalog(DbcCatalog.Parse(otherDbc).Catalog);

        vm.SelectedSignals.Should().BeEmpty();
    }

    [Fact]
    public void Clear_Removes_Samples_And_Cursor()
    {
        var vm = Create();
        vm.Select(Speed);
        vm.Ingest(new ReplayFrame(1, 0x100, 8, [0x01, 0x02], FrameFlags.None));
        vm.UpdateCursor(2);
        vm.RefreshRender();

        vm.Clear();

        vm.RenderPoints.Should().BeEmpty();
        vm.Cursor.Should().BeNull();
    }

    [Fact]
    public void UpdateCursor_Stores_Timestamp_And_Bounds()
    {
        var vm = Create();

        vm.UpdateCursor(3, 0, 10);

        vm.Cursor.Should().Be(new ChartCursor(3, 0, 10));
    }

    [Fact]
    public void RefreshRender_Raises_RenderChanged_On_Ui_Thread()
    {
        var ui = new FakeUiDispatcher();
        var raised = 0;
        var vm = new TraceChartViewModel(DbcCatalog.Parse(Dbc).Catalog, ui);
        vm.RenderChanged += (_, _) => raised++;

        vm.RefreshRender();

        raised.Should().Be(1);
    }
}

