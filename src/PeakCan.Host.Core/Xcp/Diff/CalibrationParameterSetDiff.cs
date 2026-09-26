using A2lEditor.Core.Layout;

namespace PeakCan.Host.Core.Xcp.Diff;

/// <summary>差异种类：改动 / 参数集新增 / 参数集删除。</summary>
public enum ChangeKind
{
    Changed,
    Added,
    Removed,
}

/// <summary>对象物理限值（来自 A2L 合同 LowerLimit/UpperLimit；无声明时字段为 null）。</summary>
public sealed record CalibrationLimits(double? Lower, double? Upper);

/// <summary>限值来源：对象名 → 限值（无合同/无限值返回 null）。生产侧由 ContractSet 适配。</summary>
public delegate CalibrationLimits? CalibrationLimitsLookup(string objectName);

/// <summary>
/// diff 单行：参数集中有差异的对象。值按物理值比较；单位不同也算 Changed（同值异单位是可 diff 事实）。
/// 越限只对新值判定（Removed 行新值为 null，恒不越限）；无限值来源 → 不越限。
/// </summary>
public sealed record CalibrationDiffRow(
    string Name,
    double? OldValue,
    double? NewValue,
    string? Unit,
    double? LowerLimit,
    double? UpperLimit,
    bool IsOutOfRange,
    ChangeKind Kind);

/// <summary>
/// S6-T5 参数集 diff（spec D5）：两份 <see cref="Calibration.CalibrationParameterSet"/> 的明文差异计算。
/// 纯函数、无状态：同输入两次输出逐字段一致（diff 稳定），行按对象名 Ordinal 排序，
/// 未变化对象不出现。只读展示面——应用走 S5 既有 apply 链路，本类零写入口。
/// </summary>
public static class CalibrationParameterSetDiff
{
    /// <summary>生产限值适配：从 A2L 解析期合同集取 LowerLimit/UpperLimit。</summary>
    public static CalibrationLimitsLookup FromContractSet(ContractSet contracts) =>
        name => contracts.TryGet(name, out var contract)
            ? new CalibrationLimits(contract.LowerLimit, contract.UpperLimit)
            : null;

    /// <summary>计算差异行集。baseline = 现状（原值），target = 目标（新值）。</summary>
    public static IReadOnlyList<CalibrationDiffRow> Compute(
        Calibration.CalibrationParameterSet baseline,
        Calibration.CalibrationParameterSet target,
        CalibrationLimitsLookup? limits = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(target);

        var oldEntries = baseline.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var rows = new List<CalibrationDiffRow>();
        foreach (var newName in target.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal))
        {
            var newEntry = target.Entries.First(e => e.Name == newName);
            if (oldEntries.TryGetValue(newName, out var oldEntry))
            {
                oldEntries.Remove(newName);
                if (oldEntry.Physical.Equals(newEntry.Physical)
                    && string.Equals(oldEntry.Unit, newEntry.Unit, StringComparison.Ordinal))
                {
                    continue; // 未变化：不进 diff
                }

                rows.Add(MakeRow(newName, oldEntry.Physical, newEntry.Physical, newEntry.Unit ?? oldEntry.Unit,
                    limits, ChangeKind.Changed));
            }
            else
            {
                rows.Add(MakeRow(newName, null, newEntry.Physical, newEntry.Unit, limits, ChangeKind.Added));
            }
        }

        // 仅基线有的对象 = 已删除项，同样 Ordinal 排序追加
        foreach (var oldName in oldEntries.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var oldEntry = oldEntries[oldName];
            rows.Add(new CalibrationDiffRow(
                oldName, oldEntry.Physical, null, oldEntry.Unit,
                null, null, IsOutOfRange: false, ChangeKind.Removed));
        }

        return rows;
    }

    private static CalibrationDiffRow MakeRow(
        string name, double? oldValue, double? newValue, string? unit,
        CalibrationLimitsLookup? limits, ChangeKind kind)
    {
        var range = limits?.Invoke(name);
        return new CalibrationDiffRow(
            name, oldValue, newValue, unit,
            range?.Lower, range?.Upper,
            IsOutOfRange: newValue is { } value && (
                (range?.Lower is { } lower && value < lower) ||
                (range?.Upper is { } upper && value > upper)),
            kind);
    }
}
