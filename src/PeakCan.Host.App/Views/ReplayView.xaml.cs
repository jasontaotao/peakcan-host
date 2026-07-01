using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// v1.4.0 MINOR Task 4: code-behind for the Replay tab. Keeps the
/// visual tree declarative and forwards one event (slider drag
/// completion) into the view-model.
/// <para>
/// All other bindings are <c>OneWay</c> / <c>TwoWay</c> over
/// commands + properties — no other imperative code-behind is needed
/// for the MVP. Future polish (frame table, retry button) will
/// route through additional handlers as needed.
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
}
