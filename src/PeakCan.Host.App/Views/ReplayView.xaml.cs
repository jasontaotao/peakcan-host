using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// </summary>
public partial class ReplayView : UserControl
{
    public ReplayView()
    {
        InitializeComponent();
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
