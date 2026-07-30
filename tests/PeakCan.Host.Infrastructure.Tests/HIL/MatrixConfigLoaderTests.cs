using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class MatrixConfigLoaderTests
{
    [Fact]
    public void Load_inline_ecus_parses_directly()
    {
        var json = """
        {
            "name": "Powertrain_Matrix",
            "ecus": [
                {
                    "name": "BMS",
                    "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
                    "rules": [
                        { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] }
                    ]
                },
                {
                    "name": "MCU",
                    "canIds": { "requestId": "0x7E2", "responseId": "0x7EA" },
                    "rules": []
                }
            ]
        }
        """;

        var matrix = MatrixConfigLoader.Parse(json);

        Assert.Equal("Powertrain_Matrix", matrix.Name);
        Assert.Equal(2, matrix.Ecus.Count);
        Assert.Equal("BMS", matrix.Ecus[0].Name);
        Assert.Equal("MCU", matrix.Ecus[1].Name);
    }
}
