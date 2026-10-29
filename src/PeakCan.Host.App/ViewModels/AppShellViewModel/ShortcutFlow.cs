using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// P1 全局快捷键的命令层：把窗口级 <c>KeyBinding</c> 接到既有 VM 命令上。
/// <para>
/// 这一层只做三件事——**门控、委托、取反**，不复制任何业务逻辑：
/// 真正的清空/导出/走帧仍然由 <see cref="TraceViewModel"/> / <see cref="ReplayViewModel"/>
/// 自己的命令执行，快捷键只是多一个入口。
/// </para>
/// <para>
/// 作用域策略（用户确认）：页面动作**只在目标页面激活时**生效。
/// 在 DBC 页按下 Ctrl+L 不该静默清掉后台还在录的追踪缓冲——
/// 那种"键按了、界面没反应、数据却没了"的体验比没有快捷键糟糕得多。
/// </para>
/// <para>
/// 为什么命令体内还要再判一次作用域：<c>ICommand.Execute</c> **不会**自动检查
/// <c>CanExecute</c>（本仓库用的 RelayCommand 同样不检查），所以 CanExecute 只是
/// 给界面看的"可用提示"，谁来调 Execute 都能长驱直入。
/// 作用域与前置条件因此必须在方法体里再守一次——多一行守卫的成本是零。
/// </para>
/// <para>
/// 反过来的坑同样要记住：<c>KeyBinding</c> 只要<b>手势匹配</b>就把按键标记为已处理，
/// 与 <c>CanExecute</c> 的结果无关。所以绑定一个"经常不可用"的裸键（尤其方向键）
/// 会吞掉全局的默认键盘行为。这也是走帧用 Ctrl+←/→ 而不是裸 ←/→ 的原因。
/// </para>
/// <para>
/// 刻意**不做** <c>CanExecuteChanged</c> 的高频通知：<see cref="TraceViewModel.Entries"/>
/// 每批帧都在变，订阅它去刷命令状态纯属浪费。按键时实时求值即可。
/// </para>
/// </summary>
public sealed partial class AppShellViewModel
{
    // 主区域索引常量统一放在 AppShellViewModel.cs（ViewSwitchFlow 与本文件共用）。

    private bool IsTraceTabActive => SelectedMainTabIndex == TraceTabIndex;
    private bool IsReplayTabActive => SelectedMainTabIndex == ReplayTabIndex;

    // ====================================================================
    // 追踪页：Ctrl+P 暂停 / Ctrl+L 清空 / Ctrl+E 导出可见行
    // ====================================================================

    /// <summary>Ctrl+P：暂停/继续采集。<c>IsPaused</c> 只有属性没有命令，这里做取反委托。</summary>
    [RelayCommand(CanExecute = nameof(IsTraceTabActive))]
    private void ToggleTracePause()
    {
        if (!IsTraceTabActive) return;
        _traceViewModel.IsPaused = !_traceViewModel.IsPaused;
    }

    /// <summary>Ctrl+L：清空已采集的帧（不影响连接与过滤条件）。</summary>
    [RelayCommand(CanExecute = nameof(CanClearTraceFrames))]
    private void ClearTraceFrames()
    {
        if (!CanClearTraceFrames()) return;
        _traceViewModel.ClearCommand.Execute(null);
    }

    private bool CanClearTraceFrames() => IsTraceTabActive && _traceViewModel.Entries.Count > 0;

    /// <summary>Ctrl+E：导出当前可见行（与工具栏"导出 CSV"同一个命令，同一个 SaveFileDialog）。</summary>
    [RelayCommand(CanExecute = nameof(CanExportTraceCsv))]
    private void ExportTraceCsv()
    {
        if (!CanExportTraceCsv()) return;
        _traceViewModel.ExportCsvCommand.Execute(null);
    }

    // 判据对齐真正的导出目标：命令导出的是**可见行**（EntriesView），不是缓冲区。
    // 过滤把可见行清零时按 Ctrl+E 应当无反应，而不是弹出一个"没东西可导"的空流程。
    private bool CanExportTraceCsv() => IsTraceTabActive && _traceViewModel.EntriesView.Count > 0;

    // ====================================================================
    // 回放页：←/→ 走帧、Ctrl+B 加书签
    // 原来挂在 ReplayView 的 UserControl.InputBindings 上，焦点不在回放视图里就失效。
    // ====================================================================

    /// <summary>Ctrl+←：回退一帧。</summary>
    [RelayCommand(CanExecute = nameof(CanStepReplayBack))]
    private void ReplayPrevFrame()
    {
        if (!CanStepReplayBack()) return;
        _replayViewModel.PrevFrameCommand.Execute(null);
    }

    // 前/后各问自己的命令：今天两者同源（CanStepFrame），但"到头了"这类分叉
    // 语义一旦落地，借 Next 的判据会让 ← 跟着 → 一起失效。
    private bool CanStepReplayBack()
        => IsReplayTabActive && _replayViewModel.PrevFrameCommand.CanExecute(null);

    /// <summary>Ctrl+→：前进一帧。</summary>
    [RelayCommand(CanExecute = nameof(CanStepReplayForward))]
    private void ReplayNextFrame()
    {
        if (!CanStepReplayForward()) return;
        _replayViewModel.NextFrameCommand.Execute(null);
    }

    /// <summary>走帧的前置条件由 ReplayViewModel 自己决定（已加载 + 有帧 + 未播放中）。</summary>
    private bool CanStepReplayForward()
        => IsReplayTabActive && _replayViewModel.NextFrameCommand.CanExecute(null);

    /// <summary>Ctrl+B：在当前游标处打书签。</summary>
    [RelayCommand(CanExecute = nameof(CanAddReplayBookmark))]
    private void ReplayAddBookmark()
    {
        if (!CanAddReplayBookmark()) return;
        _replayViewModel.AddBookmarkCommand.Execute(null);
    }

    private bool CanAddReplayBookmark()
        => IsReplayTabActive && _replayViewModel.AddBookmarkCommand.CanExecute(null);

    // ====================================================================
    // Ctrl+4 / Ctrl+5：补上此前根本没有入口的两个 tab
    // （"脚本" tab 从来只有点标签页一条路，XCP 同理）
    // ====================================================================

    [RelayCommand]
    private void ShowScript() => TrySelectMainTab(ScriptTabIndex);

    /// <summary>Ctrl+5：未注入 XcpViewModel 时 MainTabs 只有 4 项，命令不可用。</summary>
    [RelayCommand(CanExecute = nameof(CanShowXcp))]
    private void ShowXcp()
    {
        if (!CanShowXcp()) return;
        TrySelectMainTab(XcpTabIndex);
    }

    private bool CanShowXcp() => HasXcpTab;
}
