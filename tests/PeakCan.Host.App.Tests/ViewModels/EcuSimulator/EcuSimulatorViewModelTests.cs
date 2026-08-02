using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.EcuSimulator;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.App.Tests.ViewModels.EcuSimulator;

public class EcuSimulatorViewModelTests
{
    private const string StatesJson = """
    { "name": "Door", "initialState": "Locked",
      "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
      "states": [ { "name": "Locked", "transitions": [
        { "serviceId": "0x27", "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" } } ] } ] }
    """;

    private sealed class FileDialogStub : IFileDialogService
    {
        public string? OpenResult { get; set; }
        public string? SaveResult { get; set; }
        public string? ShowOpenDialog(string filter) => OpenResult;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => SaveResult;
    }

    private static EcuSimulatorViewModel NewVm(IFileDialogService? dlg = null)
        => new(NullLogger<EcuSimulatorViewModel>.Instance, dlg);

    [Fact]
    public void LoadFromText_Populates_Script_And_Marks_Valid()
    {
        var vm = NewVm();
        vm.LoadFromText(StatesJson).Should().BeTrue();
        vm.IsValidEcuScript.Should().BeTrue();
        vm.Script.Name.Should().Be("Door");
        vm.Script.States.Should().HaveCount(1);
    }

    [Fact]
    public void LoadFromText_Bad_Json_Returns_False_And_Sets_Error()
    {
        var vm = NewVm();
        vm.LoadFromText("{ not json").Should().BeFalse();
        vm.IsValidEcuScript.Should().BeFalse();
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HasUnsavedChanges_Tracks_Edits_After_Load()
    {
        var vm = NewVm();
        vm.LoadFromText(StatesJson);
        vm.HasUnsavedChanges.Should().BeFalse();
        vm.Script.States[0].Transitions[0].ServiceIdHex = "0x28";
        vm.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public async Task Open_Then_Save_Overwrites_Original_File()
    {
        var dir = Directory.CreateTempSubdirectory("ecusim-test");
        var path = Path.Combine(dir.FullName, "ecu.json");
        await File.WriteAllTextAsync(path, StatesJson);
        var vm = NewVm(new FileDialogStub { OpenResult = path });

        await vm.OpenCommand.ExecuteAsync(null);
        vm.Script.States[0].Transitions[0].ServiceIdHex = "0x29";
        vm.SaveCommand.Execute(null);

        var reparsed = EcuScriptLoader.Parse(File.ReadAllText(path));
        reparsed.StateMachine.Transitions[0].ServiceId.Should().Be(0x29);
        vm.StatusMessage.Should().Contain("Saved");
    }

    [Fact]
    public void GeneratorNames_Lists_Five_Builtin_Generators()
    {
        var vm = NewVm();
        vm.GeneratorNames.Should().Contain("SecurityAccessSeed");
        vm.GeneratorNames.Should().Contain("SecurityAccessVerifyKey");
        vm.GeneratorNames.Should().Contain("ClearDtc");
        vm.GeneratorNames.Should().Contain("DidReadout");
        vm.GeneratorNames.Should().Contain("DidWrite");
    }
}
