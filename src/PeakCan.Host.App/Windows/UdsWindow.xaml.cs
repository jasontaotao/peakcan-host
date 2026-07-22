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
        Unloaded += (_, _) =>
        {
            DetachLog();
            DisposeSessionVm();
        };
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

    private void DisposeSessionVm()
    {
        if (DataContext is not UdsViewModel udsVm) return;

        // C4: the Flash panel holds the per-flash secondary stack lifecycle and, for Dll
        // mode, a NATIVE OEM DLL handle (DllKeyDerivationAlgorithm owns NativeLibrary.Load
        // output). If a flash were in flight when the window closes, the stack's Dispose
        // (Detach→Client→IsoTp→DllKey) MUST run here or the native handle leaks across
        // window open/close cycles. Session panel's Dispose (TesterPresent loop) is
        // preserved verbatim; the Flash Dispose is additive on the same lifecycle boundary.
        udsVm.Session.Dispose();
        udsVm.Flash.Dispose();
    }
}
