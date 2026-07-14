using System.Windows;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Views;
using PeakCan.Host.App.Windows;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow B: View navigation (v3.11.1 PATCH M3 + earlier patches).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - All 9 Show* methods -> ViewSwitcher.Show/ShowWindow (helper, Composition namespace)
    //   - All 9 Show* methods -> CurrentView property (Flow A adjacent)
    //   - ShowTraceViewer -> _traceViewerViewModel.Reset() (cross-class)
    //   - ShowTraceViewer -> Application.Current?.MainWindow (WPF dispatcher)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    [RelayCommand]
    private void ShowTrace()
    {
        // v3.11.1 PATCH M3: extract the lazy-view-create / cache-resume
        // pattern into ViewSwitcher.Show. The original inline body
        // (4 lines including the first-show default-tab comment) is now
        // a single helper call. Show preserves the DataContext bind +
        // first-show CurrentView=null fallback behaviour (helper just
        // calls setCurrent).
        ViewSwitcher.Show(
            factory: () => new TraceView { DataContext = _traceViewModel },
            cache: ref _traceView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowTrace));
    }

    [RelayCommand]
    private void ShowDbc() => CurrentView = GetOrCreateDbcView();

    [RelayCommand]
    private void ShowSend()
    {
        // v3.11.1 PATCH M3: see ShowTrace — same ViewSwitcher extraction.
        ViewSwitcher.Show(
            factory: () => new SendView { DataContext = _sendViewModel },
            cache: ref _sendView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowSend));
    }

    [RelayCommand]
    private void ShowSignals()
    {
        // Task 16: Signal tab (DBC-decoded live signals). v3.11.1 PATCH M3:
        // extracted into ViewSwitcher — same lazy-create / cache-resume
        // behaviour, DataContext bind at first-show, DataGrid
        // virtualization state preserved across menu round-trips.
        ViewSwitcher.Show(
            factory: () => new SignalView { DataContext = _signalViewModel },
            cache: ref _signalView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowSignals));
    }

    [RelayCommand]
    private void ShowStats()
    {
        // Task 17: Stats tab (1 Hz OxyPlot charts). v3.11.1 PATCH M3:
        // extracted into ViewSwitcher — same lazy-create / cache-resume
        // behaviour. The StatsView hosts an OxyPlot.PlotView bound to
        // StatsViewModel.PlotModel; the StatisticsService pushes snapshots
        // at 1 Hz on its own thread and the VM marshals to the UI
        // dispatcher.
        ViewSwitcher.Show(
            factory: () => new StatsView { DataContext = _statsViewModel },
            cache: ref _statsView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowStats));
    }

    [RelayCommand]
    private void ShowScript()
    {
        // v1.0.0: Script tab (JavaScript automation). v3.11.1 PATCH M3:
        // extracted into ViewSwitcher. The ScriptView hosts a WebView2
        // with CodeMirror 6 editor and an output panel.
        ViewSwitcher.Show(
            factory: () => new ScriptView { DataContext = _scriptViewModel },
            cache: ref _scriptView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowScript));
    }

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
        ViewSwitcher.ShowWindow(
            factory: () => new UdsWindow { DataContext = _udsViewModel },
            cache: ref _udsWindow);
        if (_udsWindow is null) return; // defensive — cache cannot be null after ShowWindow

        if (Application.Current?.MainWindow is { } owner && owner != _udsWindow)
            _udsWindow.Owner = owner;

        if (!_udsWindow.IsVisible)
        {
            _udsWindow.Show();
        }
        else
        {
            // Already shown — bring to the foreground instead of re-activating
            // (which on Windows flashes the taskbar icon for an already-visible
            // window and looks like a bug). Same precedent as ShowTraceViewer.
            _udsWindow.Activate();
        }
    }

    // v3.50.1 PATCH-A: ShowRecord restored (reverts v3.49 Q2 which moved
    // Recording into Trace Viewer window Expander). The Record menu
    // route is back in AppShell; v1.2.11 PATCH Item 6 design preserved.
    [RelayCommand]
    private void ShowRecord()
    {
        // v1.2.11 PATCH Item 6: Recording tab. v3.11.1 PATCH M3:
        // extracted into ViewSwitcher — view is constructed on first Show
        // so the shell ctor stays STA-free.
        ViewSwitcher.Show(
            factory: () => new RecordView { DataContext = _recordViewModel },
            cache: ref _recordView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowRecord));
    }

    [RelayCommand]
    private void ShowReplay()
    {
        // v2.1.4 PATCH: Replay tab (closes the v1.4.0 MINOR orphan).
        // v3.11.1 PATCH M3: extracted into ViewSwitcher. The tab was
        // fully built (ReplayView + ReplayViewModel + IReplayService +
        // tests) but AppShell had no ShowReplayCommand and AppHostBuilder
        // had no ReplayViewModel DI registration, so the tab was
        // unreachable. ViewSwitcher.Show preserves the same lazy-create
        // + cache-resume behaviour.
        ViewSwitcher.Show(
            factory: () => new ReplayView { DataContext = _replayViewModel },
            cache: ref _replayView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowReplay));
    }

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
        var win = new MultiFrameSendWindow(_multiFrameSendViewModel);
        if (Application.Current?.MainWindow is { } owner && owner != win)
            win.Owner = owner;
        win.Show();
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
        ViewSwitcher.ShowWindow(
            factory: () => new TraceViewerView(_traceViewerViewModel),
            cache: ref _traceViewerView);
        if (_traceViewerView is null) return; // defensive — cache cannot be null after ShowWindow

        // v3.13.0 PATCH F2: hook the window's Closed event to clear
        // the singleton VM's mutable UI state on close. The VM is
        // shared with OpenSessionAsync / SaveSessionAsync (File menu),
        // so we cannot swap it per open — instead we reset its
        // observable state when the user closes the Trace Viewer
        // window. ViewSwitcher subscribes its OWN Closed handler to
        // null the cache; both fire (order doesn't matter).
        _traceViewerView.Closed += (_, _) => _traceViewerViewModel.Reset();

        // v3.9.1 PATCH Bug #1: set Owner = AppShell so closing the
        // main window cascade-closes the Trace Viewer. Without
        // Owner, Trace Viewer is an owner-less top-level Window;
        // WPF's default ShutdownMode=OnLastWindowClose keeps the
        // dispatcher running while Trace Viewer is visible, so the
        // user sees Trace Viewer survive AppShell close. Mirrors
        // OpenMultiFrame and SendViewModel's OpenMultiFrameSend
        // (SendViewModel.cs:522-525) — both already set Owner.
        // Application.Current.MainWindow is assigned to AppShell in
        // App.OnStartup.
        if (Application.Current?.MainWindow is { } owner && owner != _traceViewerView)
            _traceViewerView.Owner = owner;

        // v3.16.6 PATCH BUGFIX (defense-in-depth): WPF does not expose
        // a public IsClosed bool on Window; the "still alive" check is
        // membership in Application.Current.Windows. A closed window
        // has been removed from the collection. If we somehow hold a
        // closed reference here, drop it and let the next click rebuild.
        if (Application.Current?.Windows.Cast<Window>()
                .Any(w => ReferenceEquals(w, _traceViewerView)) != true)
        {
            _traceViewerView = null;
            return;
        }

        if (!_traceViewerView.IsVisible)
        {
            _traceViewerView.Show();
        }
        else
        {
            // Already shown — bring to the foreground instead of
            // re-activating (which on Windows flashes the taskbar
            // icon for an already-visible window and looks like a bug).
            _traceViewerView.Activate();
        }
    }

    private DbcView GetOrCreateDbcView() => _dbcView ??= new DbcView { DataContext = _dbcViewModel };
}