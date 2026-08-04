using PeakCan.HIL.Core.HIL;
using System.Text.Json;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class EcuScriptLoaderTests
{
    [Fact]
    public void Parse_loads_name_and_rules()
    {
        var json = """
        {
            "name": "BMS_Simulator",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "rules": [
                { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] },
                { "serviceId": "0x22", "dataMask": [255,255], "dataPattern": [241,144], "responseData": [98, 241, 144] },
                { "serviceId": "0x19", "subFunction": 2, "responseData": [89, 2, 8, 0, 0, 0, 9] }
            ]
        }
        """;

        var script = EcuScriptLoader.Parse(json);

        Assert.Equal("BMS_Simulator", script.Name);
        // Stateless rules converted to wildcard transitions
        var (response, _) = script.StateMachine.ProcessRequest(new byte[] { 0x3E, 0x00 });
        Assert.Equal(new byte[] { 0x7E }, response);
    }

    [Fact]
    public void ParseCanId_supports_0x_prefix()
    {
        var json = """
        {
            "name": "Test",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "rules": []
        }
        """;

        var script = EcuScriptLoader.Parse(json);
        Assert.Equal(0x7E0u, script.CanIds.ResponseId); // HIL requestId -> ECU responseId
        Assert.Equal(0x7E8u, script.CanIds.RequestId); // HIL responseId -> ECU requestId
    }

    [Fact]
    public void ParseCanId_supports_decimal()
    {
        var json = """
        {
            "name": "Test",
            "canIds": { "requestId": "2016", "responseId": "2024" },
            "rules": []
        }
        """;

        var script = EcuScriptLoader.Parse(json);
        Assert.Equal(2016u, script.CanIds.ResponseId);
        Assert.Equal(2024u, script.CanIds.RequestId);
    }

    [Fact]
    public void Parse_swaps_RequestId_ResponseId()
    {
        var json = """
        {
            "name": "Test",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "rules": []
        }
        """;

        var script = EcuScriptLoader.Parse(json);

        // HIL perspective: requestId=0x7E0 (HIL sends requests to 0x7E0), responseId=0x7E8 (HIL receives responses from 0x7E8)
        // ECU perspective: RequestId=0x7E8 (ECU sends responses to 0x7E8), ResponseId=0x7E0 (ECU receives requests on 0x7E0)
        Assert.Equal(0x7E8u, script.CanIds.RequestId);
        Assert.Equal(0x7E0u, script.CanIds.ResponseId);
    }
}
