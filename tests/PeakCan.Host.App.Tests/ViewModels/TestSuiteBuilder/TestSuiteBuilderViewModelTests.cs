using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;

namespace PeakCan.Host.App.Tests.ViewModels.TestSuiteBuilder;

public class TestSuiteBuilderViewModelTests
{
    private const string SampleSuite = """
    {
      "name": "Smoke",
      "cases": [ { "id": "c1", "name": "TP", "steps": [ { "parameters": { "$kind": "delay", "Milliseconds": 100 } } ] } ],
      "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true }
    }
    """;

    private static TestSuiteBuilderViewModel NewVm(IFileDialogService? dlg = null)
        => new(new DbcService(NullLogger<DbcService>.Instance),
            NullLogger<TestSuiteBuilderViewModel>.Instance, dlg);

    private sealed class FileDialogStub : IFileDialogService
    {
        public string? OpenResult { get; set; }
        public string? SaveResult { get; set; }
        public string? ShowOpenDialog(string filter) => OpenResult;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => SaveResult;
    }

    [Fact]
    public void LoadFromText_Populates_Cases()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        vm.Cases.Should().HaveCount(1);
        vm.Cases[0].Steps.Should().HaveCount(1);
        vm.Cases[0].Steps[0].Kind.Should().Be(TestCaseStepKind.Delay);
    }

    [Fact]
    public void ToSuite_RoundTrips_Through_HILJsonOptions()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        var json = System.Text.Json.JsonSerializer.Serialize(vm.ToSuite(), HILJsonOptions.Default);
        var reparsed = System.Text.Json.JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);
        reparsed!.Cases.Should().HaveCount(1);
        reparsed.Cases[0].Steps[0].Parameters.Should().BeOfType<DelayStep>();
    }

    [Fact]
    public void AddStep_Appends_To_Selected_Case()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        vm.SelectedCase = vm.Cases[0];
        vm.AddStepCommand.Execute(TestCaseStepKind.AssertSignal);
        vm.SelectedCase.Steps.Should().HaveCount(2);
        vm.SelectedStep.Should().Be(vm.SelectedCase.Steps[1]);
    }

    [Fact]
    public void MoveStepUp_Reorders_And_Selections_Follow()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        vm.SelectedCase = vm.Cases[0];
        vm.AddStepCommand.Execute(TestCaseStepKind.AssertSignal); // [Delay, AssertSignal]
        vm.MoveStepUpCommand.Execute(null);
        vm.SelectedCase.Steps[0].Kind.Should().Be(TestCaseStepKind.AssertSignal);
        vm.SelectedStep.Should().Be(vm.SelectedCase.Steps[0]);
    }

    [Fact]
    public void DbcLoaded_Refreshes_Signal_Options()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var doc = new PeakCan.Host.Core.Dbc.DbcDocument(
            Version: "", Nodes: new List<PeakCan.Host.Core.Dbc.Node>(),
            Messages: new List<PeakCan.Host.Core.Dbc.Message>
            {
                new(0x100, "M1", 8, "ECU1",
                    new List<PeakCan.Host.Core.Dbc.Signal> { new("Speed", 0, 16, PeakCan.Host.Core.Dbc.ByteOrder.LittleEndian, PeakCan.Host.Core.Dbc.ValueType.Unsigned, 1, 0, 0, 6553.5, "", Array.Empty<string>()) },
                    IsMultiplexed: false, MultiplexorSignalIndex: null),
            },
            MessagesById: new Dictionary<uint, PeakCan.Host.Core.Dbc.Message>(),
            ValueTables: new Dictionary<string, PeakCan.Host.Core.Dbc.ValueTable>());
        svc.SetCurrentForTests(doc);
        var vm = new TestSuiteBuilderViewModel(svc, NullLogger<TestSuiteBuilderViewModel>.Instance, null);

        vm.DbcSignals.Should().Contain(s => s.FullName == "M1.Speed");
        vm.DbcMessages.Should().HaveCount(1);
        vm.DbcMessages[0].Hex.Should().Be("0x100");
    }

    [Fact]
    public void Save_RoundTrips_Multiple_Cases_And_Steps()
    {
        var vm = NewVm();
        vm.SuiteName = "Multi";
        vm.AddCaseCommand.Execute(null);                              // case_1
        vm.AddStepCommand.Execute(TestCaseStepKind.AssertSignal);
        vm.AddStepCommand.Execute(TestCaseStepKind.Delay);
        vm.AddCaseCommand.Execute(null);                              // case_2
        vm.AddStepCommand.Execute(TestCaseStepKind.SendFrame);

        var json = System.Text.Json.JsonSerializer.Serialize(vm.ToSuite(), HILJsonOptions.Default);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);

        parsed!.Cases.Should().HaveCount(2);
        parsed.Cases[0].Steps.Should().HaveCount(2);
        parsed.Cases[1].Steps.Should().HaveCount(1);
    }

    [Fact]
    public async Task Open_Then_Add_Then_Save_Overwrites_Original_File()
    {
        var dir = Directory.CreateTempSubdirectory("peakcan-suite-test");
        var path = Path.Combine(dir.FullName, "suite.json");
        File.WriteAllText(path, SampleSuite);
        var vm = NewVm(new FileDialogStub { OpenResult = path });

        await vm.OpenCommand.ExecuteAsync(null);
        vm.AddCaseCommand.Execute(null);                              // c1 + case_2
        vm.AddStepCommand.Execute(TestCaseStepKind.SendFrame);

        vm.SaveCommand.Execute(null);

        var parsed = JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(path), HILJsonOptions.Default);
        parsed!.Cases.Should().HaveCount(2);
        vm.Status.Should().Contain("Saved");
    }

    [Fact]
    public async Task Open_Then_SaveAs_Creates_New_File()
    {
        var dir = Directory.CreateTempSubdirectory("peakcan-suite-test");
        var src = Path.Combine(dir.FullName, "suite.json");
        var dst = Path.Combine(dir.FullName, "copy.json");
        File.WriteAllText(src, SampleSuite);
        var vm = NewVm(new FileDialogStub { OpenResult = src, SaveResult = dst });

        await vm.OpenCommand.ExecuteAsync(null);
        vm.SaveAsCommand.Execute(null);

        File.Exists(dst).Should().BeTrue();
        var parsed = JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(dst), HILJsonOptions.Default);
        parsed!.Cases.Should().HaveCount(1);
        vm.Status.Should().Contain("Saved");
    }

    [Fact]
    public void InjectFault_Suite_RoundTrips_With_String_FaultType_And_Direction()
    {
        var vm = NewVm();
        vm.AddCaseCommand.Execute(null);
        vm.AddStepCommand.Execute(TestCaseStepKind.InjectFault);
        vm.SelectedStep!.SetParam("CanId", "0x100");
        vm.SelectedStep.SetParam("FaultType", "Corrupt");
        vm.SelectedStep.SetParam("Direction", "Send");
        vm.SelectedStep.SetParam("CorruptXorMask", "0x08");

        var json = JsonSerializer.Serialize(vm.ToSuite(), HILJsonOptions.Default);
        var parsed = JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);

        parsed!.Cases[0].Steps[0].Parameters.Should().BeOfType<InjectFaultStep>();
    }

    [Fact]
    public void RemoveCase_Clears_SelectedStep_And_Does_Not_Throw_On_Remove()
    {
        var vm = NewVm();
        vm.AddCaseCommand.Execute(null);                            // case_1
        vm.AddStepCommand.Execute(TestCaseStepKind.Delay);          // step lives in case_1
        vm.AddCaseCommand.Execute(null);                            // case_2
        vm.AddStepCommand.Execute(TestCaseStepKind.Delay);
        vm.SelectedCase = vm.Cases[0];
        vm.SelectedStep = vm.Cases[0].Steps[0];

        vm.RemoveCaseCommand.Execute(null);                         // deletes case_1, the holder of SelectedStep

        vm.SelectedCase.Should().Be(vm.Cases[0]);                   // case_2
        vm.SelectedStep.Should().BeNull();                          // 修复前是孤儿 step
        var act = () => vm.RemoveStepCommand.Execute(null);
        act.Should().NotThrow();                                    // 修复前 RemoveAt(-1) 抛 ArgumentOutOfRangeException
    }

    [Fact]
    public void AddCase_After_Removal_Does_Not_Reuse_Id()
    {
        var vm = NewVm();
        vm.AddCaseCommand.Execute(null);                            // case_1
        vm.AddCaseCommand.Execute(null);                            // case_2
        vm.Cases.RemoveAt(0);                                       // remove case_1

        vm.AddCaseCommand.Execute(null);

        vm.Cases.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        vm.Cases.Select(c => c.Id).Should().Contain("case_3");
    }
}
