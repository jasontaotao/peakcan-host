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
        if (e.Action != NotifyCollectionChangedAction.Add) return;
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
