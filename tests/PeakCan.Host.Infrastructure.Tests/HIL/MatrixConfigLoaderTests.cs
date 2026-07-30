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

    [Fact]
    public void Parse_with_scriptPath_loads_external_ecu_scripts()
    {
        // Arrange: create temp dir with matrix JSON + external ECU scripts
        var tempDir = Path.Combine(Path.GetTempPath(), $"matrix_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "bms.json"), """
            {
                "name": "BMS",
                "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
                "rules": [ { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] } ]
            }
            """);

            var matrixJson = """
            {
                "name": "ExtMatrix",
                "ecus": [ { "scriptPath": "bms.json" } ]
            }
            """;

            // Act
            var matrix = MatrixConfigLoader.Parse(matrixJson, tempDir);

            // Assert
            Assert.Equal("ExtMatrix", matrix.Name);
            Assert.Single(matrix.Ecus);
            Assert.Equal("BMS", matrix.Ecus[0].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Parse_scriptPath_traversal_throws()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"matrix_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var matrixJson = """
            {
                "name": "Evil",
                "ecus": [ { "scriptPath": "../../../etc/passwd" } ]
            }
            """;

            Assert.Throws<InvalidOperationException>(
                () => MatrixConfigLoader.Parse(matrixJson, tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
