using FluentAssertions;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Dbc;

public class SignalBitLayoutFixtureTests
{
    private static Message Load(string name)
    {
        var path = System.IO.Path.Combine(
            AppContext.BaseDirectory, "TestData", "Dbc", "byte-layout-demo.dbc");
        System.IO.File.Exists(path).Should().BeTrue($"fixture 必须随测试复制到 {path}");
        var doc = DbcParser.Parse(System.IO.File.ReadAllText(path)).Value;
        doc.Should().NotBeNull("演示 DBC 必须能解析");
        return doc!.Messages.Single(m => m.Name == name);
    }

    [Fact]
    public void BmsCellVolt_Mux0_OverlapsAtB4Bit0()
    {
        var snapshot = SignalBitLayout.Compute(Load("BMS_CellVolt"), 0);

        var cell = snapshot.Cells.Single(c =>
            c.ByteIndex == 4 && c.BitIndex == 0);
        cell.OwnerSignal.Should().Be("BalanceFlags");
        cell.OverlappedSignals.Should().Contain("CellVolt_2");
        snapshot.EffectiveMuxValue.Should().Be((ushort?)0);
    }

    [Fact]
    public void BmsCellVolt_Mux1_SelectsCellVolt5And6()
    {
        var snapshot = SignalBitLayout.Compute(Load("BMS_CellVolt"), 1);

        snapshot.Segments.Select(s => s.SignalName).Should()
            .Contain("CellVolt_5").And.Contain("CellVolt_6")
            .And.NotContain("CellVolt_1").And.NotContain("CellVolt_2");
    }
}
