using System.IO;
using System.Text;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Calibration;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S8-T3: writeback VM variant zone — baseline marking, delta extraction, delta loading.
/// Pure VM logic (file IO + status), no protocol connection required.
/// </summary>
public sealed class XcpWritebackVariantTests : IDisposable
{
    private readonly string _tmpDir;

    public XcpWritebackVariantTests()
    {
        _tmpDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "s8-variant-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    private static readonly string Sha = "b" + new string('0', 63);

    private string WriteJson(string fileName, string json)
    {
        var path = System.IO.Path.Combine(_tmpDir, fileName);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        return path;
    }

    private static string BaselineJson() =>
        CalibrationParameterSet.Export(
        [
            new("TorqueMax", 123.5, "Nm", null),
            new("AlphaK", 0.001, null, null),
        ], Sha, "test-baseline", DateTimeOffset.Parse("2026-09-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)).ToJson();

    private static string VariantJson() =>
        CalibrationParameterSet.Export(
        [
            new("TorqueMax", 150.0, "Nm", null),
            new("AlphaK", 0.001, null, null),
        ], Sha, "test-variant", DateTimeOffset.Parse("2026-09-27T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture)).ToJson();

    private XcpWritebackViewModel MakeVm(bool withSnapshot = false)
    {
        A2lEditor.Core.Layout.ContractSnapshot? Snap() => withSnapshot
            ? new(1, Sha, new A2lEditor.Core.Layout.A2lFingerprint(1, 0, 0, 0, 0), "0.1.0",
                  System.Array.Empty<A2lEditor.Core.Layout.SnapshottedContract>())
            : null;
        return new(snapshotFactory: Snap, directory: _tmpDir);
    }

    // --- ExtractDelta ---

    [Fact]
    public async Task ExtractDelta_produces_delta_file_with_only_diffs()
    {
        var vm = MakeVm();
        vm.BaselinePath = WriteJson("baseline.json", BaselineJson());
        vm.ParameterSetPath = WriteJson("variant.json", VariantJson());
        vm.VariantName = "sport";
        vm.DeltaPath = System.IO.Path.Combine(_tmpDir, "sport.delta.json");

        await vm.ExtractDeltaCommand.ExecuteAsync(null);

        Assert.True(File.Exists(vm.DeltaPath), "delta file should be written");
        var delta = CalibrationVariantDelta.Parse(File.ReadAllText(vm.DeltaPath!));
        Assert.Equal("sport", delta.VariantName);
        Assert.Single(delta.Entries); // only TorqueMax differs
        Assert.Equal("TorqueMax", delta.Entries[0].Name);
        Assert.Equal(150.0, delta.Entries[0].Physical);
        Assert.Contains("1 \u9879\u5dee\u5f02", vm.StatusText);
    }

    [Fact]
    public async Task ExtractDelta_without_baseline_path_shows_error()
    {
        var vm = MakeVm();
        vm.ParameterSetPath = WriteJson("variant.json", VariantJson());
        vm.VariantName = "sport";
        vm.DeltaPath = System.IO.Path.Combine(_tmpDir, "sport.delta.json");

        await vm.ExtractDeltaCommand.ExecuteAsync(null);

        Assert.Contains("\u57fa\u7ebf", vm.StatusText);
        Assert.False(File.Exists(vm.DeltaPath));
    }

    [Fact]
    public async Task ExtractDelta_a2l_mismatch_rejected()
    {
        var vm = MakeVm();
        var wrongSha = "c" + new string('0', 63);
        var wrongBaseline = CalibrationParameterSet.Export(
            [new("TorqueMax", 123.5, "Nm", null)], wrongSha, "wrong");
        vm.BaselinePath = WriteJson("wrong.json", wrongBaseline.ToJson());
        vm.ParameterSetPath = WriteJson("variant.json", VariantJson());
        vm.VariantName = "sport";
        vm.DeltaPath = System.IO.Path.Combine(_tmpDir, "sport.delta.json");

        await vm.ExtractDeltaCommand.ExecuteAsync(null);

        Assert.Contains("A2L", vm.StatusText);
        Assert.False(File.Exists(vm.DeltaPath));
    }

    // --- LoadDelta ---

    [Fact]
    public async Task LoadDelta_sets_status_with_subset_count()
    {
        var vm = MakeVm();
        // First extract a delta
        vm.BaselinePath = WriteJson("baseline.json", BaselineJson());
        vm.ParameterSetPath = WriteJson("variant.json", VariantJson());
        vm.VariantName = "sport";
        vm.DeltaPath = System.IO.Path.Combine(_tmpDir, "sport.delta.json");
        await vm.ExtractDeltaCommand.ExecuteAsync(null);

        // Now load it (needs A2L snapshot for fingerprint)
        vm = MakeVm(withSnapshot: true);
        vm.DeltaPath = System.IO.Path.Combine(_tmpDir, "sport.delta.json");
        vm.StatusText = null;
        await vm.LoadDeltaCommand.ExecuteAsync(null);

        Assert.Contains("\u53d8\u4f53 delta", vm.StatusText);
        Assert.Contains("sport", vm.StatusText);
        Assert.Contains("1", vm.StatusText); // 1 entry subset
        Assert.True(vm.IsDeltaLoaded);
    }

    [Fact]
    public async Task LoadDelta_a2l_mismatch_rejected()
    {
        var vm = MakeVm();
        var wrongShaDelta = CalibrationVariantDelta.Extract(
            "wrong", 
            CalibrationParameterSet.Export([new("X", 1.0, null, null)], "c" + new string('0', 63), "w"),
            CalibrationParameterSet.Export([new("X", 2.0, null, null)], "c" + new string('0', 63), "w"),
            "test");
        vm.DeltaPath = WriteJson("wrong.delta.json", wrongShaDelta.ToJson());

        await vm.LoadDeltaCommand.ExecuteAsync(null);

        Assert.Contains("A2L", vm.StatusText);
        Assert.False(vm.IsDeltaLoaded);
    }

    [Fact]
    public async Task LoadDelta_file_missing_shows_error()
    {
        var vm = MakeVm();
        vm.DeltaPath = System.IO.Path.Combine(_tmpDir, "nonexistent.json");

        await vm.LoadDeltaCommand.ExecuteAsync(null);

        Assert.Contains("\u4e0d\u5b58\u5728", vm.StatusText);
        Assert.False(vm.IsDeltaLoaded);
    }
}
