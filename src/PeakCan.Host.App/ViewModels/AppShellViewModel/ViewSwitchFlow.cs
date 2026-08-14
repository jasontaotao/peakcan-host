using System.Windows;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.Views;
using PeakCan.Host.App.Windows;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // ====================================================================
    // P1-6 (D5 入口语义规则): 需要与主界面持续并看 → 常驻面板/tab；独立生命周期
    // /高密度工作区/大工具 → 窗口。当前映射：
    //   主区域 tab   : 追踪 / DBC / 脚本 / 回放（低频任务）
    //   右侧常驻面板  : 发送 / 信号 / 统计（高频实时，永远与追踪同屏）
    //   独立窗口      : Trace Viewer / UDS / Multi-frame / ECU 脚本编辑器 / HIL
    //   模态对话框    : DbcTreePicker（picker，不属窗口类）；连接设置（P1-2）
    // 新增 surface 时先按此规则归类，再决定放哪。
    // ====================================================================

    // Flow B: View navigation (v3.11.1 PATCH M3 + earlier patches).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Tab Show* methods -> Selected{Main,Right}TabIndex (P1-5 dual TabControl)
    //   - Window Show* methods -> WindowHostService.Show (P0-3)
    //   - ShowTraceViewer -> _traceViewerFactory() (v3.x session-state extraction)
    //   - ShowTraceViewer -> Application.Current?.MainWindow (WPF dispatcher)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    [RelayCommand]
    private void ShowTrace() => SelectedMainTabIndex = 0;

    [RelayCommand]
    private void ShowDbc() => SelectedMainTabIndex = 1;

    [RelayCommand]
    private void ShowSend() => SelectedRightTabIndex = 0;

    [RelayCommand]
    private void ShowSignals() => SelectedRightTabIndex = 1;

    [RelayCommand]
    private void ShowStats() => SelectedRightTabIndex = 2;

    [RelayCommand]
    private void ShowScript() => SelectedMainTabIndex = 2;

    [RelayCommand]
    private void ShowUds()
    {
        // v3.11.3 PATCH: UDS migrated from an in-place UserControl tab to
        // a separate non-modal Window. Mirrors the v3.9.1 PATCH B1 + v3.11.1
        // PATCH M3 secondary-window precedent established by ShowTraceViewer:
        // factory + cache lifecycle owned by ViewSwitcher.ShowWindow
        // (auto Closed-reset); Owner + Show/Activate owned by the caller
        // (Application.Current.MainWindow only resolves inside App.OnStartup's
        // STA context).
        //
        // Behaviour parity with the pre-PATCH UserControl path:
        // - First Show creates the window from the factory.
        // - Second Show reuses the cached instance (window position + size +
        //   SelectedDid + Did/Routine/Dtc selections all preserved).
        // - Closing the window clears the cache so the next Show opens fresh.
        // - Closing AppShell cascade-closes the UDS window via the Owner
        //   assignment below (mirrors ShowTraceViewer at line 681).
        var win = _windowHost.Show(WindowKey.Uds, () => new UdsWindow { DataContext = _udsViewModel });
        if (win is null) return; // factory produced an already-closed window

        if (!win.IsVisible)
        {
            win.Show();
        }
        else
        {
            // Already shown — bring to the foreground instead of re-activating
            // (which on Windows flashes the taskbar icon for an already-visible
            // window and looks like a bug). Same precedent as ShowTraceViewer.
            win.Activate();
        }
    }

    [RelayCommand]
    private void ShowReplay() => SelectedMainTabIndex = 3;

    [RelayCommand]
    private void OpenMultiFrame()
    {
        // v2.1.7 PATCH: Multi-frame send window from the AppShell View
        // menu. Closes the v2.1.0 MINOR Pattern A2 orphan — the window
        // + VM were fully built and SendView held a button to open it,
        // but AppShell had no menu route. Each menu click opens a fresh
        // window instance pointing at the shared singleton VM (matches
        // SendViewModel's lazy-show pattern; if both menus are used,
        // two independent windows coexist — acceptable for this PATCH;
        // window-state consolidation is a separate refactor).
        // v3.11.1 PATCH M3 spec notes OpenMultiFrame as one of the 3
        // secondary-window commands using the ShowWindow path, but the
        // current behaviour opens a FRESH window on every click (no
        // cache) — preserving that semantics here means a plain
        // factory invocation is correct. If a future PATCH wants to
        // cache the window, swap to ViewSwitcher.ShowWindow with a
        // nullable cache field (matches the Trace Viewer precedent).
        // P0-3: 统一走 WindowHostService —— AppShell 与 SendView 两个入口现在
        // 共享同一单例缓存（此前 AppShell 每次新建、SendView 缓存，可并存两个窗口）。
        var win = _windowHost.Show(WindowKey.MultiFrame, () => new MultiFrameSendWindow(_multiFrameSendViewModel));
        if (win is null) return;

        if (!win.IsVisible)
            win.Show();
        else
            win.Activate();
    }

    [RelayCommand]
    private void ShowHil()
    {
        // Sprint 3: HIL testing panel (offline trace-replay + hardware-in-the-loop)
        // P0-3: HIL 迁独立窗口 —— 测试执行时需与主窗口 Trace 并排观察数据链路
        // 层通讯（user 2026-08-14 判定）。HilWindow 承载原 HilView。
        var win = _windowHost.Show(WindowKey.Hil, () => new HilWindow { DataContext = _hilViewModel });
        if (win is null) return;

        if (!win.IsVisible)
            win.Show();
        else
            win.Activate();
    }

    [RelayCommand]
    private void ShowTraceViewer()
    {
        // v3.0 MINOR Task 7: Trace Viewer non-modal window from the
        // AppShell View menu. Closes the v3.0 Pattern A orphan —
        // TraceViewerView + TraceViewerViewModel + ITraceViewerService
        // were all built in Tasks 1-6 but AppShell had no menu route.
        // **No bus writes**: this is a read-only inspection surface
        // over the loaded ASC + optional DBC. Reuses the OpenMultiFrame
        // lazy-cached-window pattern (each menu click re-shows the
        // cached window; closing resets the reference so the next
        // click opens a fresh window). The window is non-modal and not
        // owned by AppShell so the user can keep the ASC open while
        // interacting with the main tabs.
        // v3.11.1 PATCH M3: factory + cache lifecycle extracted into
        // ViewSwitcher.ShowWindow. The helper wires the Closed-reset
        // automatically (v3.9.1 PATCH B1 pattern) so the explicit
        // Closed subscription is gone. Owner assignment + Show/Activate
        // stay here because they need Application.Current.MainWindow,
        // which only resolves inside App.OnStartup's STA context.
        // P0-3: 生命周期统一走 WindowHostService（缓存/Closed 重置/Owner/IsAlive
        // 均内置于 service.Show，含 v3.16.6 死窗口防御）。会话级状态在
        // ITraceSessionService，窗口级状态随窗口实例。
        var win = _windowHost.Show(WindowKey.TraceViewer, () => new TraceViewerView(_traceViewerFactory()));
        if (win is null) return; // factory produced an already-closed window

        if (!win.IsVisible)
        {
            win.Show();
        }
        else
        {
            // Already shown — bring to the foreground instead of
            // re-activating (which on Windows flashes the taskbar
            // icon for an already-visible window and looks like a bug).
            win.Activate();
        }
    }

    // --- ECU Script Editor 独立窗口 ---

    [RelayCommand]
    private void ShowEcuScriptEditor()
    {
        var win = _windowHost.Show(WindowKey.EcuScriptEditor, () =>
        {
            var w = new EcuScriptEditorWindow(_ecuScriptEditorViewModel);
            _ecuScriptEditorViewModel.LoadInitialPath(_hilViewModel.EcuScriptPath);
            w.Closed += (_, _) => _ecuScriptEditorViewModel.Reset();
            return w;
        });
        if (win is null) return;

        if (!win.IsVisible)
            win.Show();
        else
            win.Activate();
    }

    private void OnOpenEcuEditorRequested() => ShowEcuScriptEditorCommand.Execute(null);
}
