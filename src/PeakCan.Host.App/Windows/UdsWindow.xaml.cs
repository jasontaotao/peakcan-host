using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
    private static readonly SolidColorBrush WarnBrush  = Freeze(new(Color.FromRgb(0xDC, 0xDC, 0xAA)));
    private static readonly SolidColorBrush ErrorBrush = Freeze(new(Color.FromRgb(0xF4, 0x87, 0x71)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public UdsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => OnWindowUnloaded();
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
    /// <para><b>Known follow-up (reviewer MEDIUM-1, not fixed in this PATCH — out of scope):</b>
    /// StopForWindowClose only SIGNALS cancellation of an in-flight flash run; the real
    /// secondary-stack teardown (Detach→Client→IsoTp→DllKey, which calls
    /// <c>NativeLibrary.Free</c> on the OEM DLL) runs asynchronously in the run's
    /// <c>finally</c>. If the user closes the UDS window AND immediately exits the app
    /// (so App.OnExit's <c>_host.StopAsync</c>/<c>_host.Dispose()</c> fires before the
    /// finally completes), the native handle is NOT guaranteed released before process
    /// exit (the OS reclaims it, but ungracefully). The clean fix is to thread an
    /// <c>IHostApplicationLifetime.ApplicationStopping</c> token into
    /// <c>PipelineExecutor.ExecuteAsync</c> so App.OnExit can cancel + await the flash
    /// shutdown first. Tracked as a separate concurrency-governance PATCH.
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
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachLog();
        if (e.NewValue is UdsViewModel vm)
        {
            vm.OutputLog.CollectionChanged += OnLogCollectionChanged;
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
