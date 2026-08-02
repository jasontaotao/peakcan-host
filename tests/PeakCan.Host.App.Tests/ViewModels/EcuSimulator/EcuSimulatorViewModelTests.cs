using System.IO;
using System.Text.Json;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
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

    /// <summary>始终回答 Yes —— 测试走确认分支时不卡在真实模态框。</summary>
    private sealed class MessageBoxYesStub : IMessageBoxPrompt
    {
        public Task<MessageBoxResult> ShowAsync(string title, string message, Window? owner)
            => Task.FromResult(MessageBoxResult.Yes);
        public Task<MessageBoxResult> ShowInformationAsync(string title, string message, Window? owner)
            => Task.FromResult(MessageBoxResult.OK);
    }

    private static EcuSimulatorViewModel NewVm(IFileDialogService? dlg = null, IMessageBoxPrompt? messageBox = null)
        => new(NullLogger<EcuSimulatorViewModel>.Instance, dlg, messageBox ?? new MessageBoxYesStub());

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
    public void HasUnsavedChanges_Is_False_On_Fresh_Vm_And_After_Reset()
    {
        var vm = NewVm();
        vm.HasUnsavedChanges.Should().BeFalse();
        vm.LoadFromText(StatesJson);
        vm.Script.States[0].Transitions[0].ServiceIdHex = "0x2A";
        vm.HasUnsavedChanges.Should().BeTrue();
        vm.Reset();
        vm.HasUnsavedChanges.Should().BeFalse();
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

    [Fact]
    public async Task ImportOdx_InvalidOperationException_Shows_Error_Not_Crash()
    {
        var dir = Directory.CreateTempSubdirectory("ecusim-odx");
        var odx = Path.Combine(dir.FullName, "empty.odx");   // 无 UDS 服务 → InvalidOperationException
        await File.WriteAllTextAsync(odx, "<empty/>");
        var vm = NewVm(new FileDialogStub { OpenResult = odx });
        vm.OdxEcuName = "ECU";
        vm.OdxRequestIdHex = "0x7E0";
        vm.OdxResponseIdHex = "0x7E8";

        await vm.ImportOdxCommand.ExecuteAsync(null);

        vm.IsValidEcuScript.Should().BeFalse();      // 失败不清空原有脚本
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.StatusMessage.Should().Contain("Import ODX failed");
    }

    [Fact]
    public void GeneratorNames_Comes_From_BuiltInGenerators()
    {
        var vm = NewVm();
        vm.GeneratorNames.Should().HaveCount(5);
    }
}
