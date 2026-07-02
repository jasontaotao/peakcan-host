using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PeakCan.Host.App.ViewModels.Uds;

namespace PeakCan.Host.App.Views;

/// <summary>
/// UDS diagnostic tab view. Hosts the 4-panel orchestrator's data context
/// and listens to the shared OutputLog ObservableCollection to append
/// color-coded Runs into the RichTextBox.
/// </summary>
public partial class UdsView : UserControl
{
    private static readonly SolidColorBrush WarnBrush  = Freeze(new(Color.FromRgb(0xDC, 0xDC, 0xAA)));
    private static readonly SolidColorBrush ErrorBrush = Freeze(new(Color.FromRgb(0xF4, 0x87, 0x71)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public UdsView()
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
        if (DataContext is UdsViewModel udsVm && udsVm.Session is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
