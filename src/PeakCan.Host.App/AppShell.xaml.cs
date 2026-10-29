using System.ComponentModel;
using System.Windows;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App;

/// <summary>
/// Top-level shell window. Hosts the menu (File: Open DBC, Exit / View:
/// Trace / DBC / Send), a channel-probe / connect toolbar, the status
/// bar, and a <c>ContentControl</c> bound to
/// <see cref="AppShellViewModel.CurrentView"/>.
/// <para>
/// On <see cref="OnSourceInitialized"/> we kick off
/// <see cref="AppShellViewModel.ShowTraceCommand"/> so the trace grid
/// is the default landing surface. We use <c>SourceInitialized</c>
/// (not the ctor) because the WPF dispatcher loop isn't pumping in the
/// ctor — but the STA requirement is satisfied by then.
/// </para>
/// <para>
/// P0-5: <see cref="WindowStateStore"/> (injected at startup) drives the
/// main window's geometry persistence — restore on
/// <c>SourceInitialized</c>, save on <c>Closing</c>. Secondary windows are
/// persisted inside <see cref="WindowHostService"/> instead.
/// </para>
/// </summary>
public partial class AppShell : Window
{
    /// <summary>P0-5: injected at startup by AppHostBuilder — drives the
    /// main shell's window-geometry persistence.</summary>
    public WindowStateStore? WindowStateStore { get; set; }

    /// <summary>P2-6: injected at startup by AppHostBuilder — persists the
    /// AppShell layout (right-panel width + main/right tab selection) to
    /// <c>%APPDATA%/PeakCan.Host/layout.json</c>.</summary>
    public LayoutStateStore? LayoutStateStore { get; set; }

    public AppShell()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Unsubscribe immediately — this is a one-shot initialization.
        SourceInitialized -= OnSourceInitialized;
        // P0-5: restore persisted geometry before the default tab renders.
        if (WindowStateStore is not null)
        {
            WindowHostService.ApplyStoredState(this, WindowKey.AppShell, WindowStateStore);
        }
        if (DataContext is AppShellViewModel shell)
        {
            shell.ShowTraceCommand.Execute(null);
            // 缺口 3（2026-09-17）：窗口打开即刷新 SecOC 状态指示。
            shell.RefreshSecOcStatus();
        }
        // P2-6 review r1: restore the persisted layout AFTER ShowTrace so the
        // saved main-tab selection wins over the default Trace landing tab.
        // First run (no saved layout) RestoreLayout no-ops → still lands on
        // Trace. No-op without a LayoutStateStore or a real AppShellViewModel.
        RestoreLayout();
    }

    /// <summary>
    /// 主窗口正在关闭（退出级联）。二级窗口据此跳过"关窗会中断任务"的确认：
    /// 应用退出本来就会取消在跑的任务，弹一个「否」也保不住它，只会给出假承诺。
    /// <para>
    /// 刻意做成**实例状态**而不是进程级静态：静态闩锁一旦置位就没有复位点，
    /// 万一某次关闭被取消（有订阅者 <c>e.Cancel = true</c>），整个进程的关窗拦截
    /// 就永久失效了。
    /// </para>
    /// <para>
    /// 复位**不能**放在 <c>OnActivated</c>：级联关子窗口时焦点可能回到本窗口并触发
    /// Activated，那会在子窗口的 Closing 之前把标记抹掉，拦截当场失效。改为把复位
    /// 排到消息队列末尾——那时级联已经跑完，窗口还可见就说明关闭被取消了。
    /// </para>
    /// </summary>
    internal bool IsClosing { get; set; }

    /// <summary>
    /// 退出确认用的弹窗通道。生产 DI 在 <c>WindowAndHostedServicesFlow</c> 注入
    /// 单例；默认真实现且**永不为 null**——没注入就静默放行退出，等于把最后一道门
    /// 拆掉（与 <see cref="Windows.UdsWindow.Prompt"/> 同一取舍）。
    /// </summary>
    public IMessageBoxPrompt Prompt { get; set; } = new WpfMessageBoxPrompt();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // P1 防事故最后一道门：退出应用会走到 App.OnExit，等待 5 s 后 dispose host，
        // 在跑的刷写会被取消、ECU 可能停在编程会话；脚本页里未保存的草稿也会随进程
        // 消失。先问，再置闩锁——用户点「否」时本次关闭被取消、级联不会发生，
        // IsClosing 也就不该点亮。
        if (ShouldCancelExit(Prompt, IsFlashingNow(), HasUnsavedScriptNow(), this))
        {
            e.Cancel = true;
            return;
        }
        // 退出级联会接着关闭二级窗口；先置位，让它们跳过"关窗会中断刷写"的确认
        // （那时候点「否」也救不回刷写，只会给用户一个假的选择）。
        IsClosing = true;
        // 幂等自愈：级联结束、本次关闭被取消 → 窗口还在 → 撤销标记。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible) IsClosing = false;
        }), System.Windows.Threading.DispatcherPriority.Background);
        if (WindowStateStore is not null)
        {
            WindowHostService.SaveState(this, WindowKey.AppShell, WindowStateStore);
        }
        // P2-6: persist the current layout before the window closes.
        SaveLayout();
    }

    /// <summary>当前是否正在刷写。DataContext 不是真实 VM（设计器/测试 shim）时视为否。</summary>
    private bool IsFlashingNow()
        => DataContext is AppShellViewModel shell && shell.IsFlashing;

    /// <summary>脚本页是否有未保存的改动。DataContext 不是真实 VM 时视为否。</summary>
    private bool HasUnsavedScriptNow()
        => DataContext is AppShellViewModel shell && shell.HasUnsavedScript;

    /// <summary>
    /// 退出前的危险任务确认。<c>Closing</c> 不能 await，所以必须走同步
    /// <see cref="IMessageBoxPrompt.Confirm"/>——在 UI 线程上阻塞等异步
    /// <c>ShowAsync</c> 会和 dispatcher 互等死锁（与 UdsWindow 关窗拦截同一理由）。
    /// 一次列出**所有**在进行的任务（刷写 / 未保存的脚本），与断开确认的写法一致。
    /// 都没有时直接放行：每次退出都弹一次，用户会条件反射点「是」。
    /// </summary>
    /// <returns><c>true</c> = 取消本次退出。</returns>
    internal static bool ShouldCancelExit(
        IMessageBoxPrompt prompt, bool isFlashing, bool hasUnsavedScript, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!isFlashing && !hasUnsavedScript) return false;

        var risks = new List<string>(2);
        if (isFlashing)
            risks.Add("• 正在刷写：退出会让刷写中途失败，ECU 可能停留在编程会话，严重时无法启动。");
        if (hasUnsavedScript)
            risks.Add("• 脚本页有未保存的改动：退出后这些内容会丢失。");

        return !prompt.Confirm(
            "确认退出",
            "当前有进行中的任务：\n\n" + string.Join("\n", risks) + "\n\n仍要退出吗？",
            owner);
    }

    /// <summary>P2-6: apply the persisted layout (right-panel width + tab
    /// selection) onto the shell. Silent no-op when the store is null, the
    /// DataContext is not a real <see cref="AppShellViewModel"/>, or no
    /// state has been saved yet.</summary>
    private void RestoreLayout()
    {
        if (LayoutStateStore is null || DataContext is not AppShellViewModel shell) return;
        var s = LayoutStateStore.Get();
        if (s is null) return;
        // P2-6 review r1 (LOW-3): guard against a malformed layout.json
        // ("Infinity") — GridLength throws on non-finite values. Only restore
        // finite, positive widths.
        if (s.RightPanelWidth > 0 && double.IsFinite(s.RightPanelWidth))
            RightPanelColumn.Width = new GridLength(s.RightPanelWidth);
        // 先恢复可见子集与密度，再选中标签：恢复可见性会把选中项从隐藏页移走，
        // 顺序反了会先落到隐藏页、再被兜底逻辑跳一次（状态栏多一条无谓提示）。
        shell.RestoreVisibleMainTabs(s.VisibleMainTabs);
        shell.IsCompactTables = s.CompactTables;
        shell.SelectedMainTabIndex = s.SelectedMainTabIndex;
        shell.SelectedRightTabIndex = s.SelectedRightTabIndex;
    }

    /// <summary>P2-6: capture the current layout and persist it. Silent
    /// no-op when the store is null or the DataContext is not a real
    /// <see cref="AppShellViewModel"/>.</summary>
    private void SaveLayout()
    {
        if (LayoutStateStore is null || DataContext is not AppShellViewModel shell) return;
        LayoutStateStore.Set(new LayoutStateDto(
            RightPanelColumn.Width.Value, shell.SelectedMainTabIndex, shell.SelectedRightTabIndex,
            shell.IsCompactTables, shell.PersistedVisibleMainTabs()));
    }

    /// <summary>P2-6 测试挂钩：从测试代码注入布局（真实操作由用户拖
    /// splitter / 切 tab 完成，本方法让 STA 测试直接驱动保存路径）。
    /// Visible to PeakCan.Host.App.Tests via InternalsVisibleTo.</summary>
    internal void TestSetLayout(double rightPanelWidth, int mainTab, int rightTab)
    {
        RightPanelColumn.Width = new GridLength(rightPanelWidth);
        if (DataContext is AppShellViewModel shell)
        {
            shell.SelectedMainTabIndex = mainTab;
            shell.SelectedRightTabIndex = rightTab;
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}