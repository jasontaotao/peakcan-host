using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels.Uds;

namespace PeakCan.Host.App.Windows;

/// <summary>
/// v3.11.3 PATCH: UDS diagnostic surface migrated from an in-place
/// <c>UserControl</c> tab to a separate non-modal <see cref="Window"/>.
/// Hosts the same 4-panel orchestrator (<see cref="UdsViewModel"/>) and
/// listens to the shared <c>OutputLog</c> <see cref="ObservableCollection{T}"/>
/// to append color-coded Runs into the RichTextBox. Behaviour is byte-
/// identical to the v1.1.0 UdsView.xaml.cs — only the root type and
/// namespace moved from <c>PeakCan.Host.App.Views</c> to
/// <c>PeakCan.Host.App.Windows</c>.
/// </summary>
public partial class UdsWindow : Window
{
    // Level colors mirror the Colors.xaml semantic tokens used on light
    // surfaces: WarnText #8A5B00 / Error #D62728 (the old #DCDCAA/#F48771
    // were tuned for the pre-token dark console and are near-invisible on
    // the light ConsoleBg #FBFBFC).
    private static readonly SolidColorBrush WarnBrush  = Freeze(new(Color.FromRgb(0x8A, 0x5B, 0x00)));
    private static readonly SolidColorBrush ErrorBrush = Freeze(new(Color.FromRgb(0xD6, 0x27, 0x28)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 关窗确认通道。默认真 WPF 实现，**永不为 null**——宁可弹出真实模态，
    /// 也不能因为"忘记注入"就把刷写中的关窗拦截静默跳过。宿主
    /// （AppShellViewModel.ViewSwitchFlow）会注入同一个 DI 单例，便于测试替换。
    /// </summary>
    public IMessageBoxPrompt Prompt { get; set; } = new WpfMessageBoxPrompt();

    public UdsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        Unloaded += (_, _) => OnWindowUnloaded();
    }

    /// <summary>
    /// P1 防事故：刷写中关窗 = 取消刷写（<see cref="OnWindowUnloaded"/> 会
    /// <c>Flash.StopForWindowClose()</c>）。必须让用户先确认，否则一个误点 ✕
    /// 就把擦写中的 ECU 扔在编程会话里。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 主窗口正在关 → 本窗口是被级联关掉的。此时确认框给的是假选择：
        // 点「否」也保不住刷写（App.OnExit 会取消它），所以静默放行。
        if (ShouldSkipCloseConfirm(Owner)) return;
        if (DataContext is not UdsViewModel vm) return;
        if (ShouldCancelClose(Prompt, vm.Flash.IsFlashing, this)) e.Cancel = true;
    }

    /// <summary>
    /// 退出级联期间跳过确认。判定依赖 **owner 实例**的状态（不是进程级静态闩锁）：
    /// 静态闩锁没有复位点，一次被取消的关闭就能让整个进程的拦截永久失效。
    /// </summary>
    internal static bool ShouldSkipCloseConfirm(Window? owner)
        => owner is AppShell { IsClosing: true };

    /// <summary>
    /// 关窗拦截的判定本体（静态，便于脱离 WPF 单测：不需要实例化 Window，
    /// <paramref name="owner"/> 传 null 即可）。
    /// </summary>
    /// <returns><c>true</c> = 用户否掉，应取消关闭。</returns>
    internal static bool ShouldCancelClose(IMessageBoxPrompt prompt, bool isFlashing, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!isFlashing) return false;
        return !prompt.Confirm(
            "刷写进行中",
            "关闭诊断窗口会立即中止正在进行的刷写。\n" +
            "ECU 可能停留在编程会话，严重时需要重新刷写才能恢复。\n\n" +
            "仍要关闭吗？",
            owner);
    }

    /// <summary>
    /// Window-level halt extracted from the <see cref="Unloaded"/> handler body so tests
    /// can drive it directly (WPF's RoutedEventArgs plumbing cannot be fired cleanly from
    /// a non-interactive STA test). v3.49.x PATCH (plan-uds-window-lifecycle T3): the
    /// panel VMs are DI singletons, so this must NOT Dispose them. It calls
    /// <c>StopForWindowClose</c> on Session + Flash — a window-scoped halt that stops any
    /// in-flight work (TesterPresent loop / flash run) and leaves the singleton reusable
    /// for the next window instance that binds it.
    /// <para>
    /// Before this PATCH Unloaded called <c>Session.Dispose()</c> + <c>Flash.Dispose()</c>,
    /// and Flash's one-shot <c>_disposed</c> gate froze the panel permanently after the
    /// first close (the reopened window bound the same disposed singleton →
    /// ObjectDisposedException on Start + a perpetually-greyed Start button).
    /// </para>
    /// <para><b>MEDIUM-1 (native-handle governance) — RESOLVED:</b>
    /// StopForWindowClose only SIGNALS cancellation of an in-flight flash run; the real
    /// secondary-stack teardown (Detach→Client→IsoTp→DllKey, which calls
    /// <c>NativeLibrary.Free</c> on the OEM DLL) runs asynchronously in the run's
    /// <c>finally</c>. The original race — user closes the UDS window and immediately exits
    /// the app, so App.OnExit's <c>_host.Dispose()</c> beat the finally — is closed by the
    /// concurrency-governance PATCH: <c>FlashPanelViewModel</c> links the run's CT to
    /// <c>IHostApplicationLifetime.ApplicationStopping</c> and exposes
    /// <c>CurrentRunTask</c>, and App.OnExit awaits that task (5s cap) AFTER
    /// <c>_host.StopAsync</c> and BEFORE <c>_host.Dispose()</c>.
    /// </para>
    /// <para><b>Event semantics note (reviewer LOW-2):</b> WPF <c>Unloaded</c> also fires
    /// on theme changes / re-templating, not only Close. StopForWindowClose is reversible
    /// and idempotent, so a spurious Unloaded may cancel an in-flight run (the user sees a
    /// transient Cancelled status) but leaves the VM fully reusable — no data risk, only a
    /// user-visible flash that needs a manual re-Start. Wiring <c>Closed</c> instead would couple to
    /// real window close; left as LOW cosmetic for a future PATCH since the existing surface
    /// already used Unloaded.
    /// </para>
    /// </summary>
    internal void OnWindowUnloaded()
    {
        DetachLog();
        if (DataContext is not UdsViewModel udsVm) return;

        // Stop the singleton panels' in-flight work for THIS window's lifetime, but keep
        // the VMs reusable. Session: stop TesterPresent. Flash: stop any in-flight run
        // (its finally tears the secondary stack down in Detach→Client→IsoTp→DllKey order).
        // Process-level teardown (App.OnExit DI cascade, App.xaml.cs:190) still calls the
        // real Dispose on these singletons — native OEM-DLL handles release there, not here.
        udsVm.Session.StopForWindowClose();
        udsVm.Flash.StopForWindowClose();
        // review 2026-08-29 P2: 停掉通信参数轮询（窗口重开时 DataContextChanged 再启动）。
        udsVm.Params.StopPolling();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachLog();
        if (e.NewValue is UdsViewModel vm)
        {
            vm.OutputLog.CollectionChanged += OnLogCollectionChanged;
            // review 2026-08-29 P2: 通信参数面板的 2Hz 轮询随窗口打开启动
            // （VM 是 DI 单例，不再 ctor 常驻轮询）。
            vm.Params.StartPolling();
        }
    }

    private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // v2.0.7 PATCH Bug-1: previously this handler returned early
        // for any action other than Add. ObservableCollection.Clear()
        // raises Reset (not Add), so clicking the UDS "Clear" button
        // emptied OutputLog but left the RichTextBox's FlowDocument
        // populated. WPF's binding only refreshes the bound property,
        // not the visual document — we own the LogParagraph.Inlines
        // ourselves and must mirror the collection explicitly.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                LogParagraph.Inlines.Clear();
                return;
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is null) return;
                foreach (UdsLogLine line in e.NewItems)
                {
                    var run = new Run($"[{line.Timestamp}] {line.Message}")
                    {
                        Foreground = line.Level switch
                        {
                            "Warn"  => WarnBrush,
                            "Error" => ErrorBrush,
                            _       => null,
                        }
                    };
                    LogParagraph.Inlines.Add(run);
                }
                LogBox.ScrollToEnd();

                // Trim if over the 500-line cap.
                while (LogParagraph.Inlines.Count > 500)
                {
                    LogParagraph.Inlines.Remove(LogParagraph.Inlines.FirstInline);
                }
                return;
            // Remove / Replace / Move are not currently emitted by the
            // 4 panel VMs (only Add via AppendLog + Reset via Clear).
            // Fall through to a defensive no-op rather than silently
            // leaving stale runs on screen.
        }
    }

    private void DetachLog()
    {
        if (DataContext is UdsViewModel oldVm)
        {
            oldVm.OutputLog.CollectionChanged -= OnLogCollectionChanged;
        }
    }
}
