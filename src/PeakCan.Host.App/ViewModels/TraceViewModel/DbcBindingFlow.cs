using System.Windows;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewModel
{
    /// <summary>
    /// 2026-09-06 P1-2（Bind 模式清零）：改为 <see cref="TraceViewModel(DbcService?)"/>
    /// ctor 注入后，此方法降为 ctor 内部私有接线（不再有公开属性注入入口——
    /// 消除构造后→Bind 前的半初始化窗口：期间 DbcLoaded 事件会丢，消息名列陈旧）。
    /// 未绑/未加载 DBC 时降级（符号解析报错、DBC 名列空），其余功能不受影响。
    /// </summary>
    private void BindDbc(DbcService dbc)
    {
        _dbcService = dbc;
        dbc.DbcLoaded += OnDbcLoaded;
        RefreshDbcMessageNames();
    }

    /// <summary>
    /// DBC 消息名下拉投影（<c>_dbcService.Current?.Messages</c> 名称列表）。
    /// </summary>
    private void RefreshDbcMessageNames()
    {
        DbcMessageNames.Clear();
        var doc = _dbcService?.Current;
        if (doc is null) return;
        foreach (var m in doc.Messages)
            DbcMessageNames.Add(m.Name);
    }

    /// <summary>
    /// DBC 加载完成：刷新名称投影 + 统计 DBC 名列（若展开）+ 若 DBC 名字段非空则
    /// 重解析 spec（加载后名字变可解析）。**线程封送**：<c>DbcLoaded</c> 在线程池
    /// 线程触发（<c>DbcService</c> 契约），处理器触碰 ObservableCollection / INPC /
    /// <c>EntriesView.Refresh()</c> 前必须经 dispatcher 封送回 UI 线程。
    /// </summary>
    private void OnDbcLoaded(DbcDocument doc)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // 测试/MTA 上下文无 Application：直接处理（测试单线程）。
            RefreshDbcMessageNames();
            if (StatsExpanded) RefreshStats();
            if (!string.IsNullOrWhiteSpace(DbcMessageName)) TryRebuildSpec();
            return;
        }
        dispatcher.InvokeAsync(() =>
        {
            RefreshDbcMessageNames();
            if (StatsExpanded) RefreshStats();
            if (!string.IsNullOrWhiteSpace(DbcMessageName)) TryRebuildSpec();
        });
    }
}
