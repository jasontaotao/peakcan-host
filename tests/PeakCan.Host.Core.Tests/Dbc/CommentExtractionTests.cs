using FluentAssertions;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.Core.Tests.Dbc;

public class CommentExtractionTests
{
    [Fact]
    public void Comments_Extracted_For_Standard_Message()
    {
        var dbc = "VERSION \"\"\nNS_ :\nBS_ :\nBU_: ECU1\n"
            + "BO_ 256 Msg: 8 ECU1\n"
            + " SG_ Speed : 0|16@1+ (1,0) [0|6553.5] \"km/h\" ECU1\n"
            + "CM_ BO_ 256 \"engine msg\";\n"
            + "CM_ SG_ 256 Speed \"speed signal\";\n";

        var r = DbcParser.Parse(dbc, CancellationToken.None);

        r.IsSuccess.Should().BeTrue();
        var msg = r.Value!.Messages[0];
        msg.Comment.Should().Be("engine msg");
        msg.Signals[0].Comment.Should().Be("speed signal");
    }

    [Fact]
    public void Comments_Extracted_From_Real_Vector_Dbc()
    {
        // E51_PT_CAN-BMS.dbc: Vector-generated, 31 CM_ BO_ + 174 CM_ SG_ lines.
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "E51_PT_CAN-BMS.dbc");
        var text = System.IO.File.ReadAllText(path);

        var r = DbcParser.Parse(text, CancellationToken.None);

        r.IsSuccess.Should().BeTrue("real DBC must parse");
        r.Value!.Messages.Should().NotBeEmpty();
        r.Value.Messages.Should().Contain(m => !string.IsNullOrEmpty(m.Comment),
            "CM_ BO_ comments must be extracted");
        r.Value.Messages.Should().Contain(m => m.Signals.Any(s => !string.IsNullOrEmpty(s.Comment)),
            "CM_ SG_ comments must be extracted");
    }
}
