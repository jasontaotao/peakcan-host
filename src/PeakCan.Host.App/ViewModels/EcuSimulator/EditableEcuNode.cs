using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>
/// 可编辑 ECU 脚本节点基类：属性/集合变化向上冒泡到 <see cref="EditableEcuScript.Changed"/>,
/// 供 VM 触发 HasUnsavedChanges 重估。
/// </summary>
public abstract class EditableEcuNode : ObservableObject
{
    internal Action? Notify;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Notify?.Invoke();
    }

    internal void HookCollection(INotifyCollectionChanged c)
        => c.CollectionChanged += (_, _) => Notify?.Invoke();
}
