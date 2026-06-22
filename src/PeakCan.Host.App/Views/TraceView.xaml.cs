using System.Collections.Specialized;
using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Trace tab view. The control hosts a virtualized
/// <c>DataGrid</c> bound to <see cref="ViewModels.TraceViewModel.Entries"/>.
/// <para>
/// <b>v0.9.2 auto-scroll:</b> when the user is at the bottom of the grid,
/// new rows automatically scroll into view. If the user scrolls up to
/// inspect history, auto-scroll pauses until they return to the bottom.
/// </para>
/// </summary>
public partial class TraceView : UserControl
{
    private bool _autoScroll = true;

    public TraceView()
    {
        InitializeComponent();
        TraceGrid.Loaded += (_, _) =>
        {
            if (TraceGrid.ItemsSource is INotifyCollectionChanged cc)
            {
                cc.CollectionChanged += OnCollectionChanged;
            }
        };
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _autoScroll)
        {
            TraceGrid.ScrollIntoView(TraceGrid.Items[^1]);
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Auto-scroll is on when the user is within 1 row-height of the bottom.
        // Use the event args instead of casting sender (which may be
        // the DataGrid, not the ScrollViewer).
        _autoScroll = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 20;
    }
}
