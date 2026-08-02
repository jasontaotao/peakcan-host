using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>一个状态 = 名称 + 该状态的转移列表。</summary>
public sealed partial class EditableEcuState : EditableEcuNode
{
    [ObservableProperty] private string _name = "";
    public ObservableCollection<EditableEcuTransition> Transitions { get; } = new();

    public static EditableEcuState FromTransitions(
        string name, IEnumerable<EcuStateTransition> transitions, EditableEcuScript owner)
    {
        var s = new EditableEcuState { Name = name };
        s.Notify = owner.Notify;
        s.HookCollection(s.Transitions);
        foreach (var t in transitions)
            s.Transitions.Add(EditableEcuTransition.FromTransition(t, owner.Notify));
        return s;
    }
}
