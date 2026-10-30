using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow: 主窗口的显示偏好——主标签可见子集 + 表格密度。
    // 两者都由 AppShell.SaveLayout / RestoreLayout 持久化到 layout.json。
    //
    // 主标签隐藏刻意**保持绝对索引**：TabControl 仍绑 MainTabs 全集，隐藏项只是
    // 折叠（ItemContainerStyle 绑 TabSpec.IsVisible）。这样 SelectedMainTabIndex、
    // 快捷键 Ctrl+1..5、持久化语义全部不变，不需要"逻辑索引 ↔ 可见索引"的映射——
    // 那种映射一旦和持久化/快捷键任一处漂移，就会出现"按 Ctrl+3 跳到别的页"。

    // ---------- 主标签可见子集 ----------

    private bool _showTraceTab = true;
    private bool _showDbcTab = true;
    private bool _showReplayTab = true;
    private bool _showScriptTab = true;
    private bool _showXcpTab = true;

    /// <summary>视图 → 主标签 → 追踪。取消勾选即从标签栏隐藏（不影响数据）。</summary>
    public bool ShowTraceTab
    {
        get => _showTraceTab;
        set => SetMainTabVisible(TraceTabIndex, value, ref _showTraceTab, nameof(ShowTraceTab));
    }

    /// <summary>视图 → 主标签 → DBC。</summary>
    public bool ShowDbcTab
    {
        get => _showDbcTab;
        set => SetMainTabVisible(DbcTabIndex, value, ref _showDbcTab, nameof(ShowDbcTab));
    }

    /// <summary>视图 → 主标签 → 回放。</summary>
    public bool ShowReplayTab
    {
        get => _showReplayTab;
        set => SetMainTabVisible(ReplayTabIndex, value, ref _showReplayTab, nameof(ShowReplayTab));
    }

    /// <summary>视图 → 主标签 → 脚本。</summary>
    public bool ShowScriptTab
    {
        get => _showScriptTab;
        set => SetMainTabVisible(ScriptTabIndex, value, ref _showScriptTab, nameof(ShowScriptTab));
    }

    /// <summary>视图 → 主标签 → XCP（未注入 XcpViewModel 时该项不存在，始终不可见）。</summary>
    public bool ShowXcpTab
    {
        get => _showXcpTab;
        set => SetMainTabVisible(XcpTabIndex, value, ref _showXcpTab, nameof(ShowXcpTab));
    }

    /// <summary>是否注入了 XCP 主标签。没注入时菜单项要禁用（勾了也没有页可显示）。</summary>
    public bool HasXcpTab => MainTabs.Count > XcpTabIndex;

    private int VisibleMainTabCount() => MainTabs.Count(t => t.IsVisible);

    private int FirstVisibleMainTabIndex()
    {
        for (var i = 0; i < MainTabs.Count; i++)
        {
            if (MainTabs[i].IsVisible) return i;
        }
        return 0;
    }

    private void SetMainTabVisible(int index, bool value, ref bool field, string propertyName)
    {
        if (field == value) return;
        // 没注入 XCP 时第 5 项根本不存在——静默保持"不可见"，不要抛。
        if (index >= MainTabs.Count) return;

        if (!value && VisibleMainTabCount() <= 1)
        {
            // 菜单的复选项已经翻过去了，必须通知一次把它弹回来。
            StatusMessage = "至少要保留一个主标签";
            OnPropertyChanged(propertyName);
            return;
        }

        field = value;
        MainTabs[index].IsVisible = value;
        OnPropertyChanged(propertyName);

        // 藏掉的正是当前页 → 移到第一个可见页；否则内容区会停在一个没有标签头的页上。
        if (!value && SelectedMainTabIndex == index)
            SelectedMainTabIndex = FirstVisibleMainTabIndex();
    }

    /// <summary>
    /// 选中项落到隐藏标签上时的兜底（持久化恢复、外部直接赋值都会走到）。
    /// </summary>
    partial void OnSelectedMainTabIndexChanged(int value)
    {
        // ctor 早期 MainTabs 还没赋值（属性只读、一次性赋值），先放行。
        if (MainTabs is null || MainTabs.Count == 0) return;

        // 越界：layout.json 被手改 / 从 5 标签版本降级到 4 标签。TabControl 拿到非法
        // SelectedIndex 不会抛，但主区域会空白，而且退出时坏值又被写回去。
        if (value < 0 || value >= MainTabs.Count)
        {
            SelectedMainTabIndex = FirstVisibleMainTabIndex();
            return;
        }

        if (MainTabs[value].IsVisible) return;

        StatusMessage = $"「{MainTabs[value].Header}」标签已隐藏（视图 → 主标签 可恢复）";
        SelectedMainTabIndex = FirstVisibleMainTabIndex();
    }

    /// <summary>菜单与快捷键共用的切换入口：隐藏的标签不切过去，只提示。</summary>
    internal bool TrySelectMainTab(int index)
    {
        if (index < 0 || index >= MainTabs.Count) return false;
        if (!MainTabs[index].IsVisible)
        {
            StatusMessage = $"「{MainTabs[index].Header}」标签已隐藏（视图 → 主标签 可恢复）";
            return false;
        }
        SelectedMainTabIndex = index;
        return true;
    }

    /// <summary>持久化：全部可见时返回 null（旧 layout.json 缺字段同样表示"全显示"）。</summary>
    internal string? PersistedVisibleMainTabs()
        => VisibleMainTabCount() == MainTabs.Count
            ? null
            : string.Concat(MainTabs.Where(t => t.IsVisible).Select(t => t.Key));

    /// <summary>
    /// 恢复持久化的可见子集。未知 key / 空串直接忽略——宁可回到"全显示"，
    /// 也不要把所有标签藏光（那会得到一个空标签栏且无从恢复）。
    /// </summary>
    internal void RestoreVisibleMainTabs(string? keys)
    {
        if (string.IsNullOrEmpty(keys)) return;

        var wanted = new HashSet<char>(keys);
        if (!MainTabs.Any(t => t.Key.Length > 0 && wanted.Contains(t.Key[0]))) return;

        for (var i = 0; i < MainTabs.Count; i++)
        {
            var visible = MainTabs[i].Key.Length > 0 && wanted.Contains(MainTabs[i].Key[0]);
            MainTabs[i].IsVisible = visible;
            SetShowField(i, visible);
        }

        OnPropertyChanged(nameof(ShowTraceTab));
        OnPropertyChanged(nameof(ShowDbcTab));
        OnPropertyChanged(nameof(ShowReplayTab));
        OnPropertyChanged(nameof(ShowScriptTab));
        OnPropertyChanged(nameof(ShowXcpTab));

        // 只修"选中了隐藏页"；越界由 OnSelectedMainTabIndexChanged 统一兜底，
        // 这里不做无边界索引访问（调用顺序一变就会抛）。
        if (SelectedMainTabIndex >= 0 && SelectedMainTabIndex < MainTabs.Count
            && !MainTabs[SelectedMainTabIndex].IsVisible)
        {
            SelectedMainTabIndex = FirstVisibleMainTabIndex();
        }
    }

    private void SetShowField(int index, bool value)
    {
        switch (index)
        {
            case TraceTabIndex: _showTraceTab = value; break;
            case DbcTabIndex: _showDbcTab = value; break;
            case ReplayTabIndex: _showReplayTab = value; break;
            case ScriptTabIndex: _showScriptTab = value; break;
            case XcpTabIndex: _showXcpTab = value; break;
        }
    }

    // ---------- 表格密度 ----------

    /// <summary>
    /// 紧凑行高。真源是全局资源 <c>DataGridRowHeight</c>：改资源即可让所有
    /// DataGrid 立即跟随（视图里不再写本地 RowHeight 覆盖）。
    /// 持久化在 layout.json，启动时由 AppShell.RestoreLayout 赋值。
    /// </summary>
    [ObservableProperty]
    private bool _isCompactTables;

    partial void OnIsCompactTablesChanged(bool value)
    {
        if (Application.Current is { } app)
            app.Resources["DataGridRowHeight"] = value ? 18.0 : 22.0;
    }
}
