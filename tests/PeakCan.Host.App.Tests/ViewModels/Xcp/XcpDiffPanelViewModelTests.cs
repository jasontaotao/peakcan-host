using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Diff;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

public class XcpDiffPanelViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    private static CalibrationEntry E(string name, double physical, string? unit = null) => new(name, physical, unit, null);

    private static CalibrationParameterSet Set(params CalibrationEntry[] entries) =>
        CalibrationParameterSet.Export(entries, "AAAA", "test", T0);

    private static XcpDiffPanelViewModel Vm(
        Func<string, CalibrationParameterSet>? loader = null,
        Func<string?>? sha = null,
        Func<CalibrationLimitsLookup?>? limits = null)
    {
        loader ??= _ => Set();
        return new XcpDiffPanelViewModel(
            loadSet: (path, _) => Task.FromResult(loader(path)),
            currentA2lSha256Provider: sha,
            limitsLookupProvider: limits);
    }

    [Fact]
    public async Task Missing_paths_shows_status_and_skips_loader()
    {
        var called = false;
        var vm = Vm(_ => { called = true; return Set(); });

        await vm.LoadAndDiffCommand.ExecuteAsync(null);

        Assert.False(called);
        Assert.Equal("请先填写基线与目标参数集文件路径", vm.StatusText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task LoadAndDiff_populates_diff_rows_with_summary()
    {
        var vm = Vm(path => path.Contains("base")
            ? Set(E("KmChanged", 1.0, "Nm"), E("KmSame", 3.0))
            : Set(E("KmChanged", 1.5, "Nm"), E("KmSame", 3.0)));
        vm.BaselinePath = "D:/tmp/base.json";
        vm.TargetPath = "D:/tmp/target.json";

        await vm.LoadAndDiffCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("KmChanged", row.Name);
        Assert.Equal(ChangeKind.Changed, row.Kind);
        Assert.Equal(1.0, row.OldValue);
        Assert.Equal(1.5, row.NewValue);
        Assert.Contains("diff 完成", vm.StatusText);
        Assert.Contains("1 项差异", vm.StatusText);
    }

    [Fact]
    public async Task Out_of_range_comes_from_limits_lookup()
    {
        var vm = Vm(
            path => path.Contains("base") ? Set(E("KmLim", 1.0)) : Set(E("KmLim", 99.0)),
            limits: () => new CalibrationLimitsLookup(name => new CalibrationLimits(0.0, 10.0)));
        vm.BaselinePath = "base.json";
        vm.TargetPath = "target.json";

        await vm.LoadAndDiffCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(vm.Rows).IsOutOfRange);
    }

    [Fact]
    public async Task Fingerprint_mismatch_warns_but_still_computes()
    {
        var vm = Vm(
            path => path.Contains("base") ? Set(E("KmA", 1.0)) : Set(E("KmA", 2.0)),
            sha: () => "BBBB");
        vm.BaselinePath = "base.json";
        vm.TargetPath = "target.json";

        await vm.LoadAndDiffCommand.ExecuteAsync(null);

        Assert.Single(vm.Rows);
        Assert.Contains("指纹", vm.StatusText);
    }

    [Fact]
    public async Task Loader_failure_is_reported_not_thrown()
    {
        var vm = Vm(_ => throw new InvalidOperationException("参数集 JSON 无效：boom"));
        vm.BaselinePath = "base.json";
        vm.TargetPath = "target.json";

        await vm.LoadAndDiffCommand.ExecuteAsync(null);

        Assert.Contains("加载失败", vm.StatusText);
        Assert.Contains("boom", vm.StatusText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Locate_command_raises_card_locate_requested()
    {
        var vm = Vm();
        string? requested = null;
        vm.CardLocateRequested += name => requested = name;
        var row = new CalibrationDiffRow("KmX", 1.0, 2.0, null, null, null, false, ChangeKind.Changed);

        vm.LocateCommand.Execute(row);

        Assert.Equal("KmX", requested);
    }
}
