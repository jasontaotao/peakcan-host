using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class SignalCatalogTests
{
    private const string Dbc = """
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
         SG_ EngineTemp : 16|8@1+ (1,-40) [0|215] "C" Vector__XXX

        BO_ 2147484672 ExtendedData: 8 ECM
         SG_ Speed : 0|16@1+ (0.1,0) [0|0] "kph" Vector__XXX
        """;

    [Fact]
    public void FromDbc_Builds_Message_And_Signal_Catalog()
    {
        var parsed = DbcCatalog.Parse(Dbc, "engine.dbc");
        parsed.Error.Should().BeNull();
        var catalog = SignalCatalog.FromDbc(parsed.Catalog!);

        catalog.Messages.Should().HaveCount(2);
        catalog.Messages.Should().Contain(m => m.Name == "EngineData"
            && m.CanId == 0x100
            && !m.IsExtended);
        var engine = catalog.Messages.Single(m => m.Name == "EngineData");
        engine.Signals.Select(s => s.Name).Should().Equal("EngineSpeed", "EngineTemp");
        catalog.Messages.Should().Contain(m => m.Name == "ExtendedData"
            && m.CanId ==  0x400
            && m.IsExtended
            && m.Signals.Single().Name == "Speed");
    }

    [Fact]
    public void TryDecodeSignal_Returns_Numeric_Value()
    {
        var catalog = SignalCatalog.FromDbc(DbcCatalog.Parse(Dbc).Catalog!);

        var ok = catalog.TryDecodeSignal(0x100, false, [0x01, 0x02], 2, "EngineSpeed", out var value);

        ok.Should().BeTrue();
        value.Should().Be(128.25);
    }

    [Fact]
    public void TryDecodeSignal_Resolves_Extended_Id()
    {
        var catalog = SignalCatalog.FromDbc(DbcCatalog.Parse(Dbc).Catalog!);

        var ok = catalog.TryDecodeSignal(0x400, true, [0x0A, 0x00], 2, "Speed", out var value);

        ok.Should().BeTrue();
        value.Should().Be(1);
    }

    [Theory]
    [InlineData("MissingSignal")]
        public void TryDecodeSignal_Rejects_Missing_Signal(string signalName)
    {
        var catalog = SignalCatalog.FromDbc(DbcCatalog.Parse(Dbc).Catalog!);

        var ok = catalog.TryDecodeSignal(0x100, false, [0x01, 0x02], 2, signalName, out var value);

        ok.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryDecodeSignal_Rejects_Unknown_Can_Id()
    {
        var catalog = SignalCatalog.FromDbc(DbcCatalog.Parse(Dbc).Catalog!);

        catalog.TryDecodeSignal(0x101, false, [0x01, 0x02], 2, "EngineSpeed", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryDecodeSignal_Rejects_When_Payload_Too_Short()
    {
        var catalog = SignalCatalog.FromDbc(DbcCatalog.Parse(Dbc).Catalog!);

        catalog.TryDecodeSignal(0x100, false, [0x01], 1, "EngineSpeed", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryDecodeSignal_Respects_Multiplexor()
    {
        const string multiplexedDbc = """
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 300 MuxData: 8 ECM
             SG_ MuxSelector M : 0|8@1+ (1,0) [0|1] "" Vector__XXX
             SG_ LowValue m0 : 8|8@1+ (1,0) [0|255] "" Vector__XXX
             SG_ HighValue m1 : 8|8@1+ (1,0) [0|255] "" Vector__XXX
            """;
        var catalog = SignalCatalog.FromDbc(DbcCatalog.Parse(multiplexedDbc).Catalog!);

        catalog.TryDecodeSignal(0x12C, false, [0x01, 0x42], 2, "HighValue", out var value)
            .Should().BeTrue();
        value.Should().Be(66);

        catalog.TryDecodeSignal(0x12C, false, [0x00, 0x42], 2, "HighValue", out _)
            .Should().BeFalse();
    }
}








