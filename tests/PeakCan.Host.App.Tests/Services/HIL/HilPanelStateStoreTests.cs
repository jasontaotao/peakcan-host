using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.App.Services.HilPanel;
using Xunit;

namespace PeakCan.Host.App.Tests.ServicesSuitePreflight;

public sealed class HilPanelStateStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"hil-panel-{Guid.NewGuid():N}.json");

    private static HilPanelStateDto MakePanelState() => new(
        SelectedMode: nameof(HilMode.Hardware),
        DbcPath: @"C:\dbc.dbc",
        SuitePath: @"C:\suite.json",
        TracePath: @"C:\trace.asc",
        EcuScriptPath: @"C:\ecu.json",
        MatrixPath: @"C:\matrix.json",
        CaseLogDirectory: @"C:\logs",
        EnableFaultInjection: true,
        CaptureCaseLogs: false,
        EnableAnalyze: true,
        SelectedCaseIds: ["case_1"]);

    [Fact]
    public void Set_ThenGet_RoundTripsSelectedCaseIds()
    {
        var store = new HilPanelStateStore(NullLogger<HilPanelStateStore>.Instance, TempPath());
        var dto = MakePanelState();
        store.Set(dto);
        store.Get().Should().Be(dto);
    }

    [Fact]
    public async Task Reload_Restores_Dto()
    {
        var path = TempPath();
        new HilPanelStateStore(NullLogger<HilPanelStateStore>.Instance, path).Set(MakePanelState());
        var reloaded = new HilPanelStateStore(NullLogger<HilPanelStateStore>.Instance, path);
        await reloaded.LoadAsync(default);
        reloaded.Get().Should().BeEquivalentTo(MakePanelState());
    }

    [Fact]
    public async Task Missing_File_Returns_Null()
    {
        var store = new HilPanelStateStore(NullLogger<HilPanelStateStore>.Instance, TempPath());
        await store.LoadAsync(default);
        store.Get().Should().BeNull();
    }

    [Fact]
    public async Task Corrupt_File_Returns_Null()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not json");
        var store = new HilPanelStateStore(NullLogger<HilPanelStateStore>.Instance, path);
        await store.LoadAsync(default);
        store.Get().Should().BeNull();
    }

    [Fact]
    public async Task Oversized_File_Is_Treated_As_Empty()
    {
        var path = TempPath();
        File.WriteAllText(path, new string('x', (int)HilPanelStateStore.MaxLoadFileBytes + 1));
        var store = new HilPanelStateStore(NullLogger<HilPanelStateStore>.Instance, path);
        await store.LoadAsync(default);
        store.Get().Should().BeNull();
    }
}
