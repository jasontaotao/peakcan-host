using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 支持批量替换的 <see cref="ObservableCollection{T}"/>：一次性从头部移除 N 项 +
/// 追加尾部 M 项，**只发一条 <c>CollectionChanged(Reset)</c> 通知**。
/// <para>
/// <b>为什么需要它：</b>WPF 的 <c>DataGrid</c> 对每次 <c>CollectionChanged</c> 都在
/// UI 线程上同步做视图维护（行映射、索引协调、可见区重算）。Trace 在 500 fps 下
/// 每 200 ms 一批约 100 帧，逐帧 <c>Add</c> + 逐帧 <c>RemoveAt(0)</c> 会产生约
/// 200 次通知/批；这些通知以 <c>DispatcherPriority.Normal</c> 投递，而
/// <c>Input(5)</c> / <c>Render(7)</c> 优先级**低于** Normal(9)，于是输入与绘制被
/// 长期饿死——表现为"界面点不动、画面不刷新，但 CPU 单核繁忙"。
/// 合并成 1 次通知后，通知次数与帧率解耦。
/// </para>
/// <para>
/// <b>为什么用 Reset 而不是范围 Add/Remove：</b><see cref="System.Windows.Data.ListCollectionView"/>
/// 明确不支持范围操作（实测抛 <c>NotSupportedException: 不支持范围操作</c>）。
/// </para>
/// <para>
/// <b>语义：</b>与"逐条 Add 再逐条 RemoveAt(0)"最终状态完全一致；差别只在通知次数。
/// </para>
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// 原子应用一批变更：先从头部移除 <paramref name="removeCount"/> 项，再追加
    /// <paramref name="toAppend"/>。整个过程只发一条 <c>Reset</c>。
    /// 两者都为 0 时是 no-op（不发任何通知）。
    /// </summary>
    /// <param name="removeCount">
    /// 从头部移除的项数。超过当前 <see cref="Collection{T}.Count"/> 时按实际数量截断。
    /// </param>
    /// <param name="toAppend">追加到尾部的项（按给定顺序）。</param>
    public void ApplyBatch(int removeCount, IReadOnlyList<T> toAppend)
    {
        ArgumentNullException.ThrowIfNull(toAppend);

        if (removeCount <= 0 && toAppend.Count == 0) return;

        using (BlockReentrancy())
        {
            if (removeCount > 0)
            {
                if (removeCount > Count) removeCount = Count;
                for (var i = 0; i < removeCount; i++)
                    Items.RemoveAt(0);
            }

            for (var i = 0; i < toAppend.Count; i++)
                Items.Add(toAppend[i]);

            // Reset 的契约：Count / Item[] 都可能已变，两个属性通知都要补发，
            // 再发一次 Reset 让视图整体重建（WPF 只认这一条）。
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
