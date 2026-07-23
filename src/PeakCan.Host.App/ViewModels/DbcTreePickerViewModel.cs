using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v3.16.0 MINOR: a single node in the DBC message tree displayed by
/// <c>DbcTreePickerWindow</c>. Either a message (with a non-null
/// <see cref="SignalName"/>) or a signal (non-null
/// <see cref="CanId"/> + <see cref="SignalName"/>, parent points to
/// the message). Carries the original DBC values for the picker to
/// round-trip back to <c>TraceViewerViewModel.AddToWatch</c>.
/// </summary>
public sealed class DbcTreeNode : INotifyPropertyChanged
{
    public uint? CanId { get; }
    public string? MessageName { get; }
    public string? SignalName { get; }
    public string? Unit { get; }
    public bool IsMessage => SignalName is null;
    public bool IsSignal => SignalName is not null;
    public ObservableCollection<DbcTreeNode> Children { get; } = new();

    public DbcTreeNode(uint? canId, string? messageName, string? signalName = null, string? unit = null)
    {
        CanId = canId;
        MessageName = messageName;
        SignalName = signalName;
        Unit = unit;
        _isVisible = true;
    }

    /// <summary>v3.61.0 PATCH: search filter visibility. Set by the
    /// ViewModel when SearchText changes; TreeView ItemContainerStyle
    /// binds to this via DataTrigger. When collapsed, TreeView hides
    /// the node and its descendants automatically.</summary>
    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>v3.16.0: search filter — true if this node OR any
    /// descendant matches. Used by the picker to hide non-matching
    /// subtrees.</summary>
    public bool Matches(string search)
    {
        if (string.IsNullOrEmpty(search)) return true;
        var s = search.Trim();
        if (string.IsNullOrEmpty(s)) return true;
        if (MessageName is not null && MessageName.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        if (SignalName is not null && SignalName.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in Children)
            if (c.Matches(s)) return true;
        return false;
    }
}

/// <summary>
/// v3.16.0 MINOR: view-model for the DBC tree picker dialog. Walks
/// <c>DbcService.Current.Messages</c> into a hierarchical
/// <see cref="DbcTreeNode"/> tree; user picks one or more signals
/// (via checkbox) and clicks OK → window returns the selection.
/// </summary>
public sealed partial class DbcTreePickerViewModel : ObservableObject
{
    private readonly DbcDocument? _doc;

    /// <summary>Hierarchical tree of message → signal nodes.</summary>
    public ObservableCollection<DbcTreeNode> Roots { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>v3.16.0 MINOR: user-selected signals. Updated as the
    /// user checks/unchecks signal nodes in the tree. The dialog
    /// window reads this on OK click.</summary>
    public ObservableCollection<DbcTreeNode> SelectedSignals { get; } = new();

    public DbcTreePickerViewModel(DbcDocument? doc)
    {
        _doc = doc;
        BuildTree();
    }

    private void BuildTree()
    {
        Roots.Clear();
        if (_doc is null) return;
        foreach (var msg in _doc.Messages)
        {
            var maskedId = msg.Id & 0x7FFFFFFFu;
            var msgNode = new DbcTreeNode(maskedId, msg.Name);
            foreach (var sig in msg.Signals)
            {
                var sigNode = new DbcTreeNode(maskedId, msg.Name, sig.Name, sig.Unit);
                msgNode.Children.Add(sigNode);
            }
            Roots.Add(msgNode);
        }
    }

    /// <summary>v3.61.0 PATCH: apply search filter to the tree. Sets
    /// IsVisible on each node recursively so the TreeView hides
    /// non-matching items via ItemContainerStyle DataTrigger. Empty
    /// search restores full visibility.</summary>
    partial void OnSearchTextChanged(string value)
    {
        foreach (var root in Roots)
            ApplyFilter(root, value);
    }

    private static bool ApplyFilter(DbcTreeNode node, string search)
    {
        var selfMatch = string.IsNullOrEmpty(search)
            || (node.MessageName is not null && node.MessageName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            || (node.SignalName is not null && node.SignalName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

        var anyChildMatch = false;
        foreach (var child in node.Children)
        {
            if (ApplyFilter(child, search))
                anyChildMatch = true;
        }

        node.IsVisible = selfMatch || anyChildMatch;
        return node.IsVisible;
    }

    /// <summary>v3.16.0 MINOR: toggle a signal's selection. Called by
    /// the checkbox column in the picker TreeView.</summary>
    public void ToggleSelection(DbcTreeNode node)
    {
        if (!node.IsSignal) return;  // only signals are selectable
        if (SelectedSignals.Contains(node))
            SelectedSignals.Remove(node);
        else
            SelectedSignals.Add(node);
    }

    /// <summary>v3.16.0 MINOR: returns the selection as (canId, signalName)
    /// tuples for the caller to pass to
    /// <c>TraceViewerViewModel.AddToWatch</c>.</summary>
    public IReadOnlyList<(uint CanId, string SignalName)> GetSelectedTuples()
    {
        var list = new List<(uint, string)>();
        foreach (var node in SelectedSignals)
        {
            if (node.CanId is { } id && node.SignalName is { } name)
                list.Add((id, name));
        }
        return list;
    }
}