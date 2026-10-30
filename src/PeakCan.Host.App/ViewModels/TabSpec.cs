using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// P1-5: one tab in the dual-TabControl main area / right panel. The view is
/// created lazily on first <see cref="View"/> access — keeps the shell ctor
/// STA-free (xunit runs MTA) and defers heavy views like ScriptView's WebView2
/// until the tab is first selected. The cached instance is reused on tab
/// round-trips (DataGrid virtualization state / WebView2 survive).
/// <para>
/// P2：主标签可隐藏（用户只关心其中几个时减少噪音）。<see cref="Key"/> 是持久化用的
/// 稳定标识——**不能用索引**，否则以后在中间插一个标签，旧 layout.json 会把别的页藏掉。
/// </para>
/// </summary>
public sealed partial class TabSpec : ObservableObject
{
    public string Header { get; }

    /// <summary>持久化标识（主标签用单字母；右侧面板不需要，为空）。</summary>
    public string Key { get; }

    /// <summary>是否显示在 TabControl 里（隐藏的标签仍保留索引，避免选中项错位）。</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>
    /// 标题旁是否显示"未保存"圆点。目前只有脚本页会用（AppShellViewModel 订阅
    /// 脚本 VM 的 HasUnsavedChanges 后写入）；其它标签恒为 false，圆点自动折叠。
    /// 刻意不改 <see cref="Header"/> 本身——它是只读的，而且脏标记不该污染持久化标识。
    /// </summary>
    [ObservableProperty]
    private bool _isDirty;

    private readonly Func<object> _factory;
    private object? _view;

    public object View => _view ??= _factory();

    public TabSpec(string header, Func<object> factory, string key = "")
    {
        Header = header;
        _factory = factory;
        Key = key;
    }
}
