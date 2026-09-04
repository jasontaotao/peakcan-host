using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class DbcSignalsEncodingTests
{
    internal static DbcDocument CreateTestDbc()
    {
        var text = """
VERSION ""

NS_ :

BS_:

BU_: Charger BMS

BO_ 512 CRM: 8 Charger
 SG_ CRM_Signal : 0|16@1+ (1,0) [0|65535] "" BMS
""";
        var result = DbcParser.Parse(text);
        Assert.True(result.IsSuccess, result.Error?.Message ?? "parse failed");
        return result.Value!;
    }

    internal static RestbusNode CreateDbcNode() => new()
    {
        Name = "Charger",
        Identity = new RawCanNodeIdentity(),
        Messages =
        [
            new NodeMessage(
                new CanMessageRef(512, false),
                100,
                new DbcSignalsSource("CRM"))
        ]
    };

    [Fact]
    public void SetSignalValue_GetEncodedPayload_ReturnsEncodedBytes()
    {
        var dbc = CreateTestDbc();
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance, dbc);
        runtime.Start([CreateDbcNode()], null);

        runtime.SetSignalValue("Charger", "CRM", "CRM_Signal", 100);
        var payload = runtime.GetEncodedPayload("Charger", "CRM");

        Assert.NotNull(payload);
        Assert.Equal(8, payload.Length);
        // Little-endian, factor 1, offset 0 → bytes [0x64, 0x00, ...]
        Assert.Equal(0x64, payload[0]);
        runtime.Stop();
    }

    [Fact]
    public void SetSignalValue_UnknownNode_DoesNotThrow()
    {
        var dbc = CreateTestDbc();
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance, dbc);
        runtime.Start([CreateDbcNode()], null);
        runtime.SetSignalValue("Nonexistent", "CRM", "CRM_Signal", 1);
        runtime.Stop();
    }

    [Fact]
    public void GetEncodedPayload_UnknownMessage_ReturnsNull()
    {
        var dbc = CreateTestDbc();
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance, dbc);
        runtime.Start([CreateDbcNode()], null);
        Assert.Null(runtime.GetEncodedPayload("Charger", "Nonexistent"));
        runtime.Stop();
    }
}