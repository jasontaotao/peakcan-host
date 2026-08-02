using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
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

    private static TestSuiteBuilderViewModel NewVm()
        => new(new DbcService(NullLogger<DbcService>.Instance),
            NullLogger<TestSuiteBuilderViewModel>.Instance, null);

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
}
