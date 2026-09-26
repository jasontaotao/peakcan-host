using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Diff;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Diff;

public class CalibrationParameterSetDiffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedOrder = ["KmAdded", "KmChanged", "KmGone"];

    private static CalibrationEntry E(string name, double physical, string? unit = null) => new(name, physical, unit, null);

    private static CalibrationParameterSet Set(params CalibrationEntry[] entries) =>
        CalibrationParameterSet.Export(entries, "AAAA", "test", T0);

    [Fact]
    public void Compute_changed_added_removed_rows_are_stable_and_ordered()
    {
        var baseline = Set(E("KmChanged", 1.0, "Nm"), E("KmGone", 2.0), E("KmSame", 3.0));
        var target = Set(E("KmAdded", 9.5, "kg"), E("KmChanged", 1.5, "Nm"), E("KmSame", 3.0));

        var rows = CalibrationParameterSetDiff.Compute(baseline, target);
        var again = CalibrationParameterSetDiff.Compute(baseline, target);

        // 同输入两次结果一致（diff 稳定钉）
        Assert.Equal(rows.Select(r => r.ToString()), again.Select(r => r.ToString()));
        // 对象名 Ordinal 排序，未变化对象不出现
        Assert.Equal(ExpectedOrder, rows.Select(r => r.Name).ToArray());

        var added = Assert.Single(rows, r => r.Name == "KmAdded");
        Assert.Equal(ChangeKind.Added, added.Kind);
        Assert.Null(added.OldValue);
        Assert.Equal(9.5, added.NewValue);
        Assert.Equal("kg", added.Unit);

        var changed = Assert.Single(rows, r => r.Name == "KmChanged");
        Assert.Equal(ChangeKind.Changed, changed.Kind);
        Assert.Equal(1.0, changed.OldValue);
        Assert.Equal(1.5, changed.NewValue);
        Assert.Equal("Nm", changed.Unit);

        var removed = Assert.Single(rows, r => r.Name == "KmGone");
        Assert.Equal(ChangeKind.Removed, removed.Kind);
        Assert.Equal(2.0, removed.OldValue);
        Assert.Null(removed.NewValue);

        Assert.DoesNotContain(rows, r => r.Name == "KmSame");
    }

    [Fact]
    public void Out_of_range_uses_limits_lookup_on_new_value()
    {
        var baseline = Set(E("KmLim", 1.0));
        var over = Set(E("KmLim", 99.0));
        var within = Set(E("KmLim", 4.0));

        CalibrationLimits? Lookup(string name) => new(Lower: 0.0, Upper: 10.0);

        var overRow = Assert.Single(CalibrationParameterSetDiff.Compute(baseline, over, Lookup));
        Assert.True(overRow.IsOutOfRange);
        Assert.Equal(0.0, overRow.LowerLimit);
        Assert.Equal(10.0, overRow.UpperLimit);

        var okRow = Assert.Single(CalibrationParameterSetDiff.Compute(Set(E("KmLim", 1.0)), within, Lookup));
        Assert.False(okRow.IsOutOfRange);

        // 无限值来源（lookup 返回 null）→ 永不越限，限值列为空
        var noLimits = Assert.Single(CalibrationParameterSetDiff.Compute(baseline, over, _ => null));
        Assert.False(noLimits.IsOutOfRange);
        Assert.Null(noLimits.LowerLimit);
        Assert.Null(noLimits.UpperLimit);
    }

    [Fact]
    public void Same_value_different_unit_is_changed_row()
    {
        var baseline = Set(E("KmUnit", 1.0, "Nm"));
        var target = Set(E("KmUnit", 1.0, "kg"));
        var row = Assert.Single(CalibrationParameterSetDiff.Compute(baseline, target));
        Assert.Equal(ChangeKind.Changed, row.Kind);
        Assert.Equal(1.0, row.OldValue);
        Assert.Equal(1.0, row.NewValue);
        Assert.Equal("kg", row.Unit);
    }
}
