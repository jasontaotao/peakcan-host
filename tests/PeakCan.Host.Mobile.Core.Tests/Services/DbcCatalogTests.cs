using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class DbcCatalogTests
{
    private const string Dbc = """
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
         SG_ EngineTemp : 16|8@1+ (1,-40) [0|215] "C" Vector__XXX
        """;

    [Fact]
    public void Parse_Loads_Message_And_Decodes_Standard_Id()
    {
        var result = DbcCatalog.Parse(Dbc, "engine.dbc");

        result.Error.Should().BeNull();
        var catalog = result.Catalog!;
        var decoded = catalog.Decode(0x100, isExtended: false, [0x01, 0x02, 0x03], dlc: 3);

        decoded!.MessageName.Should().Be("EngineData");
        decoded.Signals.Should().Contain(s => s.Name == "EngineSpeed" && s.Value == "128.25" && s.Unit == "rpm");
        decoded.Signals.Should().Contain(s => s.Name == "EngineTemp" && s.Value == "-37" && s.Unit == "C");
    }

    [Fact]
    public void Decode_Resolves_Extended_Id()
    {
        const string extendedDbc = """
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 2147483904 ExtendedData: 8 ECM
             SG_ Speed : 0|16@1+ (0.1,0) [0|0] "kph" Vector__XXX
            """;
        var catalog = DbcCatalog.Parse(extendedDbc).Catalog!;

        var decoded = catalog.Decode(0x100, isExtended: true, [0x0A, 0x00], dlc: 2);

        decoded!.MessageName.Should().Be("ExtendedData");
        decoded.Signals[0].Value.Should().Be("1");
    }

    [Fact]
    public void Parse_Returns_Error_Without_Throwing()
    {
        var result = DbcCatalog.Parse("not dbc");
        result.Catalog.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Decode_Selects_Multiplexed_Signals_By_Active_Mux_Value()
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
        var catalog = DbcCatalog.Parse(multiplexedDbc).Catalog!;

        var decoded = catalog.Decode(0x12C, isExtended: false, [0x01, 0x42], dlc: 2);

        decoded!.MessageName.Should().Be("MuxData");
        decoded.Signals.Should().ContainSingle(s => s.Name == "MuxSelector");
        decoded.Signals.Should().Contain(s => s.Name == "HighValue" && s.Value == "66");
        decoded.Signals.Should().NotContain(s => s.Name == "LowValue");
    }
}