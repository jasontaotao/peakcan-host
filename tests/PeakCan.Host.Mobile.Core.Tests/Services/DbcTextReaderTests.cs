using System.Text;
using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class DbcTextReaderTests
{
    [Fact]
    public async Task Read_Prefers_Utf8_Without_Bom()
    {
        const string dbc = "SG_ 电压 : 0|8@1+ (1,0) [0|255] \"V\" Vector__XXX";

        using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(dbc));

        var result = await DbcTextReader.ReadAsync(stream);
        result.Should().Contain("电压");
    }

    [Fact]
    public async Task Read_Falls_Back_To_Gb18030_For_Legacy_Chinese_Dbc()
    {
        const string dbc = "BO_ 100 车速报文: 8 ECM\n SG_ 行驶状态 : 0|8@1+ (1,0) [0|255] \"状态\" Vector__XXX\nVAL_ 100 行驶状态 0 \"停止\" 1 \"充电\";";

        // Initialize the code-page provider through the reader before arranging bytes.
        using var warmup = new MemoryStream("\n"u8.ToArray());
        _ = await DbcTextReader.ReadAsync(warmup);

        var bytes = Encoding.GetEncoding("GB18030").GetBytes(dbc);
        using var stream = new MemoryStream(bytes);

        var result = await DbcTextReader.ReadAsync(stream);
        result.Should().Contain("车速报文");
        result.Should().Contain("行驶状态");
        result.Should().Contain("充电");
    }
}
