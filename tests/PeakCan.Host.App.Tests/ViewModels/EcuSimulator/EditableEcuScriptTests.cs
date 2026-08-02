using System.Text.Json;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.EcuSimulator;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.App.Tests.ViewModels.EcuSimulator;

public class EditableEcuScriptTests
{
    private const string StatesJson = """
    {
      "name": "Door",
      "initialState": "Locked",
      "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
      "states": [
        { "name": "Locked", "transitions": [
          { "serviceId": "0x27", "subFunction": "0x01",
            "dataMask": [255], "dataPattern": [1],
            "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" },
            "toState": "Unlocked", "responseDelayMs": 10 } ] },
        { "name": "wildcard", "transitions": [
          { "serviceId": "0x3E", "subFunction": null,
            "response": { "$type": "static", "data": [126] },
            "responseDelayMs": 0 } ] }
      ]
    }
    """;

    [Fact]
    public void FromEcuScript_Reverses_CanIds_To_File_Perspective()
    {
        var script = EcuScriptLoader.Parse(StatesJson);
        var e = EditableEcuScript.FromEcuScript(script);

        // loader swapped: ECU.RequestId = file responseId(0x7E8), ECU.ResponseId = file requestId(0x7E0)
        e.RequestIdHex.Should().Be("0x7E0");
        e.ResponseIdHex.Should().Be("0x7E8");
        e.Name.Should().Be("Door");
        e.InitialState.Should().Be("Locked");
    }

    [Fact]
    public void FromEcuScript_Groups_Transitions_By_State_And_Reads_Response_Modes()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));

        e.States.Should().HaveCount(2);
        var locked = e.States.First(s => s.Name == "Locked");
        locked.Transitions.Should().HaveCount(1);
        var t = locked.Transitions[0];
        t.ServiceIdHex.Should().Be("0x27");
        t.SubFunctionHex.Should().Be("0x01");
        t.DataMaskHex.Should().Be("FF");
        t.DataPatternHex.Should().Be("01");
        t.ResponseMode.Should().Be(EcuResponseMode.Dynamic);
        t.GeneratorName.Should().Be("SecurityAccessSeed");
        t.ToState.Should().Be("Unlocked");
        t.ResponseDelayMs.Should().Be(10);

        var w = e.States.First(s => s.Name == "wildcard");
        w.Transitions[0].ResponseMode.Should().Be(EcuResponseMode.Static);
        w.Transitions[0].StaticDataHex.Should().Be("7E");
        w.Transitions[0].SubFunctionHex.Should().BeNullOrEmpty();
    }

    [Fact]
    public void FromEcuScript_Loads_DidValues_And_Rules_Migrates_To_Wildcard()
    {
        const string rulesJson = """
        { "name": "B", "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
          "didValues": { "0xF190": [1, 2] },
          "rules": [ { "serviceId": "0x22", "responseData": [98, 241] } ] }
        """;
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(rulesJson));

        e.States.Should().HaveCount(1);               // rules → wildcard 迁移
        e.States[0].Name.Should().Be("wildcard");
        e.States[0].Transitions[0].ServiceIdHex.Should().Be("0x22");
        e.DidValues.Should().ContainSingle();
        e.DidValues[0].KeyHex.Should().Be("0xF190");
        e.DidValues[0].BytesHex.Should().Be("01 02");
    }

    [Fact]
    public void Changing_A_Property_Raises_Changed_Event()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));
        var raised = 0;
        e.Changed += () => raised++;
        e.States[0].Transitions[0].ServiceIdHex = "0x28";
        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ToJson_RoundTrips_Through_Loader_Without_Data_Loss()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));
        var outJson = e.ToJson();

        var reparsed = EcuScriptLoader.Parse(outJson);
        reparsed.Name.Should().Be("Door");
        reparsed.InitialState.Should().Be("Locked");
        // 文件视角 canIds 反交换回来 = 原文件视角
        reparsed.CanIds.RequestId.Should().Be(0x7E8);   // ECU 视角; 文件 requestId 0x7E0 → ECU ResponseId
        reparsed.CanIds.ResponseId.Should().Be(0x7E0);
        reparsed.StateMachine.Transitions.Should().BeEquivalentTo(
            EcuScriptLoader.Parse(StatesJson).StateMachine.Transitions);
    }

    [Fact]
    public void ToJson_Emits_Response_As_Type_Discriminator()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));
        var outJson = e.ToJson();
        using var doc = System.Text.Json.JsonDocument.Parse(outJson);
        var resp = doc.RootElement.GetProperty("states")[0]
            .GetProperty("transitions")[0].GetProperty("response");
        resp.GetProperty("$type").GetString().Should().Be("dynamic");
        resp.GetProperty("generatorName").GetString().Should().Be("SecurityAccessSeed");
    }
}
