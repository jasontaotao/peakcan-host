using System.Text.Json;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class EcuScriptLoaderStatefulTests
{
    [Fact]
    public void ParseEcuScript_StatefulJson_ParsesStatesAndTransitions()
    {
        var json = """
        {
            "name": "BMS_Secure",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "states": [
                {
                    "name": "locked",
                    "transitions": [
                        { "serviceId": "0x27", "subFunction": 1, "response": { "$type": "static", "data": [103, 1, 17, 34] }, "toState": "seedSent" },
                        { "serviceId": "0x2E", "response": { "$type": "static", "data": [127, 46, 34] } }
                    ]
                },
                {
                    "name": "seedSent",
                    "transitions": [
                        { "serviceId": "0x27", "subFunction": 2, "response": { "$type": "static", "data": [103, 2] }, "toState": "unlocked" }
                    ]
                }
            ]
        }
        """;

        var script = EcuScriptLoader.Parse(json);

        Assert.Equal("BMS_Secure", script.Name);
        Assert.NotNull(script.StateMachine);
        // Verify the state machine works: send 0x27 subFunc 1 from default state
        // (no transition matches "default" state, so NRC 0x11)
        var (response, _) = script.StateMachine.ProcessRequest(new byte[] { 0x27, 0x01 });
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x11 }, response);
    }

    [Fact]
    public void ParseEcuScript_StatelessJson_ConvertsViaFromRules()
    {
        var json = """
        {
            "name": "BMS",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "rules": [
                { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] }
            ]
        }
        """;

        var script = EcuScriptLoader.Parse(json);

        Assert.Equal("BMS", script.Name);
        Assert.NotNull(script.StateMachine);
        // Stateless rules become wildcard transitions — should match from any state
        var (response, _) = script.StateMachine.ProcessRequest(new byte[] { 0x3E, 0x00 });
        Assert.Equal(new byte[] { 0x7E }, response);
    }

    [Fact]
    public void ParseEcuScript_SwapsCanIds_ToEcuPerspective()
    {
        var json = """
        {
            "name": "Test",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "rules": []
        }
        """;

        var script = EcuScriptLoader.Parse(json);

        // HIL requestId=0x7E0 -> ECU responseId=0x7E0
        // HIL responseId=0x7E8 -> ECU requestId=0x7E8
        Assert.Equal(0x7E8u, script.CanIds.RequestId);
        Assert.Equal(0x7E0u, script.CanIds.ResponseId);
    }

    [Fact]
    public void ParseEcuScript_Throws_WhenBothStatesAndRulesPresent()
    {
        var json = """
        {
            "name": "Bad",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "states": [],
            "rules": []
        }
        """;

        Assert.Throws<JsonException>(() => EcuScriptLoader.Parse(json));
    }
}
