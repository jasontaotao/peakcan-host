using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using A2lEditor.Core;
using A2lEditor.Core.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// S3-T9: one node in the XCP object picker tree. Either a category group
/// (<see cref="IsCategory"/>, children = objects of that A2L category) or an
/// object leaf (carries the (name, category) pair the picker round-trips back
/// into <see cref="ITraceSessionService.XcpWatchedObjects"/>).
/// <para>
/// Node semantics mirror <c>DbcTreeNode</c> (DbcTreePickerWindow precedent):
/// <see cref="IsVisible"/> drives the TreeView search-filter DataTrigger;
/// <see cref="IsSelected"/> drives the leaf checkbox.
/// </para>
/// </summary>
public sealed class XcpPickerNode : INotifyPropertyChanged
{
    /// <summary>Group node: category text (如 MEASUREMENT)。Leaf node: A2L 对象名。</summary>
    public string Name { get; }

    /// <summary>对象类别（组节点为 null；叶节点为 A2L 类别文本，与 XcpWatchRow.Category 同形）。</summary>
    public string? Category { get; }

    public bool IsCategory => Category is null;
    public bool IsLeaf => Category is not null;

    public ObservableCollection<XcpPickerNode> Children { get; } = new();

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            NotifyChanged();
        }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            NotifyChanged();
        }
    }

    public XcpPickerNode(string name, string? category = null)
    {
        Name = name;
        Category = category;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Search filter: true when empty search, or this node's name (or any
    /// descendant's name) contains the search text case-insensitively.
    /// </summary>
    public bool Matches(string search)
    {
        var s = search.Trim();
        if (s.Length == 0) return true;
        if (Name.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        return Children.Any(c => c.Matches(s));
    }

    private void NotifyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// S3-T9: view-model for the XCP object picker dialog (spec §3 批量挑变量).
/// Walks the loaded <see cref="ContractSet"/> into a category-grouped tree;
/// user checks objects and confirms → the selection is reconciled into
/// <see cref="ITraceSessionService.XcpWatchedObjects"/> (dedup on the
/// (name, category) pair) and returned for callers to consume (T11 wiring
/// feeds XcpCardPanelViewModel.AddWatch).
/// <para>
/// 空合同集 / 未加载 A2L → 空树，不炸（确认恒返回空列表）。VM 层全部可测，
/// 窗口壳（XcpObjectPickerWindow）只消费。
/// </para>
/// </summary>
public sealed partial class XcpObjectPickerViewModel : ObservableObject
{
    private readonly ITraceSessionService? _traceSession;
    private readonly List<XcpPickerNode> _leafNodes = new();

    /// <summary>类别分组树（组节点 → 对象叶节点）。</summary>
    public ObservableCollection<XcpPickerNode> Roots { get; } = new();

    /// <summary>搜索过滤文本（名称子串，大小写不敏感；空 = 不过滤）。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// 可空注入构造（保既有 VM 测试构造模式）。contracts 为 null 表示
    /// A2L 未加载（空树态）；traceSession 为 null 表示对账目标未接线（纯选择模式）。
    /// </summary>
    public XcpObjectPickerViewModel(ContractSet? contracts, ITraceSessionService? traceSession = null)
    {
        _traceSession = traceSession;
        BuildTree(contracts);
        SeedWatchedSelection();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter(value);

    /// <summary>
    /// 确认：输出勾选的 (name, category) 行；traceSession 已接线时把缺失行
    /// 回填进 <see cref="ITraceSessionService.XcpWatchedObjects"/>（按对去重，
    /// 二次确认不产生重复行）。返回全量勾选行（含此前已关注的），调用方
    ///（T11 卡片接线）按自身语义消费——卡片 AddWatch 自带按名去重。
    /// </summary>
    public IReadOnlyList<XcpWatchRow> Confirm()
    {
        var rows = _leafNodes
            .Where(n => n.IsSelected)
            .Select(n => new XcpWatchRow(n.Name, n.Category!))
            .ToList();

        if (_traceSession is not null)
        {
            var watched = _traceSession.XcpWatchedObjects;
            foreach (var row in rows)
            {
                if (!watched.Any(w => w.Name == row.Name && w.Category == row.Category))
                    watched.Add(row);
            }
        }

        return rows;
    }

    private void BuildTree(ContractSet? contracts)
    {
        Roots.Clear();
        _leafNodes.Clear();
        if (contracts is null) return;

        // 固定类别顺序（A2L 关键字序）：MEASUREMENT → CHARACTERISTIC → AXIS_PTS。
        var groups = new (A2lObjectCategory Category, string Text)[]
        {
            (A2lObjectCategory.Measurement, "MEASUREMENT"),
            (A2lObjectCategory.Characteristic, "CHARACTERISTIC"),
            (A2lObjectCategory.AxisPts, "AXIS_PTS"),
        };

        foreach (var (category, text) in groups)
        {
            var objects = contracts.All
                .Where(c => c.Category == category)
                .Select(c => c.ObjectName)
                .ToList();
            if (objects.Count == 0) continue;

            var group = new XcpPickerNode(text);
            foreach (var name in objects)
            {
                var leaf = new XcpPickerNode(name, text);
                group.Children.Add(leaf);
                _leafNodes.Add(leaf);
            }
            Roots.Add(group);
        }
    }

    /// <summary>已关注对象默认勾选（按 (name, category) 对账，同名异类不误标）。</summary>
    private void SeedWatchedSelection()
    {
        if (_traceSession is null) return;
        var watched = _traceSession.XcpWatchedObjects;
        foreach (var leaf in _leafNodes)
        {
            if (watched.Any(w => w.Name == leaf.Name && w.Category == leaf.Category))
                leaf.IsSelected = true;
        }
    }

    private void ApplyFilter(string search)
    {
        foreach (var root in Roots)
            ApplyFilter(root, search);
    }

    private static void ApplyFilter(XcpPickerNode node, string search)
    {
        node.IsVisible = node.Matches(search);
        foreach (var child in node.Children)
            ApplyFilter(child, search);
    }
}
