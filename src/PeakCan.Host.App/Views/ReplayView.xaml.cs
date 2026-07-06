using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// v1.4.0 MINOR Task 4: code-behind for the Replay tab. Keeps the
/// visual tree declarative and forwards a couple of events (slider
/// drag completion + Open Recent dropdown) into the view-model.
/// <para>
/// All other bindings are <c>OneWay</c> / <c>TwoWay</c> over
/// commands + properties — no other imperative code-behind is needed.
/// </para>
/// <para>
/// v3.9.0 MINOR P3: Slider visual markers. Subscribes to the VM's
/// <see cref="ReplayViewModel.Bookmarks"/> + <see cref="ReplayViewModel.LoopRegions"/>
/// CollectionChanged events and <see cref="ReplayViewModel.ScrubberMaxValue"/>
/// PropertyChanged; on any change, redraws the marker Canvas overlay
/// with a triangle per bookmark + a colored band per loop region.
/// </para>
/// </summary>
public partial class ReplayView : UserControl
{
    public ReplayView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private ReplayViewModel? _vm;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Unsubscribe from the old VM's notifications (defensive — the
        // control is a singleton in the Replay tab, so the DataContext
        // shouldn't actually change at runtime, but the dispose pattern
        // protects against the test-injection case where a different
        // VM is wired up).
        if (_vm is not null)
        {
            ((INotifyCollectionChanged)_vm.Bookmarks).CollectionChanged -= OnMarkersChanged;
            ((INotifyCollectionChanged)_vm.LoopRegions).CollectionChanged -= OnMarkersChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as ReplayViewModel;
        if (_vm is not null)
        {
            // v3.9.0 P3: subscribe to Bookmarks/LoopRegions mutations
            // and ScrubberMaxValue changes (the proportional positions
            // depend on TotalDuration). On any change, redraw the
            // markers. The events fire on the UI thread (the
            // ObservableCollection mutations are from RelayCommand
            // handlers, all on the dispatcher).
            ((INotifyCollectionChanged)_vm.Bookmarks).CollectionChanged += OnMarkersChanged;
            ((INotifyCollectionChanged)_vm.LoopRegions).CollectionChanged += OnMarkersChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        RedrawMarkers();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReplayViewModel.ScrubberMaxValue))
        {
            RedrawMarkers();
        }
    }

    private void OnMarkersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RedrawMarkers();

    /// <summary>
    /// v3.9.0 MINOR P3: redraw the marker overlay. Positions are
    /// proportional to <c>Timestamp / ScrubberMaxValue</c> mapped to
    /// the canvas's ActualWidth. Bookmarks are 8×8 triangles pointing
    /// down (fill = yellow); loop regions are 4px-tall bands (fill
    /// = semi-transparent cyan). The canvas itself is layered on top
    /// of the native Slider with IsHitTestVisible=False so all
    /// mouse events pass through (preserves v3.9.0 P2 click-to-jump
    /// + v3.8.0 keyboard arrow bindings + touch).
    /// </summary>
    private void RedrawMarkers()
    {
        if (_vm is null) return;
        MarkerCanvas.Children.Clear();

        var width = MarkerCanvas.ActualWidth;
        var total = _vm.ScrubberMaxValue;
        if (width <= 0 || total <= 0) return;

        // Loop regions: colored band per region. Semi-transparent so
        // the underlying Slider track is still visible.
        foreach (var region in _vm.LoopRegions)
        {
            var startX = (region.Start / total) * width;
            var endX = (region.End / total) * width;
            var band = new Rectangle
            {
                Width = Math.Max(0, endX - startX),
                Height = 4,
                Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0xC8, 0xFF)),  // cyan @ 50%
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(band, startX);
            Canvas.SetTop(band, 2);
            MarkerCanvas.Children.Add(band);
        }

        // Bookmarks: small triangle pointing down. Width 8, height 8.
        foreach (var bookmark in _vm.Bookmarks)
        {
            var x = (bookmark.Timestamp / total) * width;
            var triangle = new Polygon
            {
                Points = new PointCollection
                {
                    new(x - 4, 0),
                    new(x + 4, 0),
                    new(x, 8),
                },
                Fill = Brushes.Gold,
                Stroke = Brushes.DarkGoldenrod,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            MarkerCanvas.Children.Add(triangle);
        }
    }

    /// <summary>
    /// Translate slider drag completion into a SeekTo command. The
    /// slider's <c>Value</c> TwoWay binding has already pushed the
    /// position to <c>CurrentTimestamp</c> via the binding system;
    /// we also dispatch the <c>SeekToCommand</c> so the service's
    /// timeline cursor jumps and the timer re-anchors.
    /// </summary>
    private void Scrubber_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is ReplayViewModel vm)
        {
            vm.SeekToCommand.Execute(Scrubber.Value);
        }
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: pop a <see cref="ContextMenu"/> rooted at
    /// the Open Recent button. Each <see cref="MenuItem"/> binds to
    /// <see cref="ReplayViewModel.OpenRecentSessionCommand"/> with the
    /// entry's path as CommandParameter. A "Clear recent" footer
    /// invokes <see cref="ReplayViewModel.ClearRecentSessionsCommand"/>.
    /// <para>
    /// When the list is empty we still show the Clear Recent item so
    /// the user has a path to clean up dead legacy entries — with a
    /// single disabled "(no recent sessions)" placeholder above it so
    /// the surface doesn't look buggy.
    /// </para>
    /// </summary>
    private void OnOpenRecentClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReplayViewModel vm) return;
        var menu = new ContextMenu();
        if (vm.RecentSessionEntries.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(no recent sessions)", IsEnabled = false });
        }
        else
        {
            foreach (var entry in vm.RecentSessionEntries)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = entry.Label,
                    Command = vm.OpenRecentSessionCommand,
                    CommandParameter = entry.Path,
                });
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Clear recent",
            Command = vm.ClearRecentSessionsCommand,
        });
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
