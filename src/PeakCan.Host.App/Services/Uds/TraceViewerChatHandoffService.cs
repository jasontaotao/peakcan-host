using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Views;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// Window glue between the UDS log analysis page and the Trace Viewer chat.
/// Window lifetime (caching, geometry, menu entry) stays inside
/// <see cref="WindowHostService"/>; this class only routes the payload.
/// </summary>
public sealed class TraceViewerChatHandoffService(
    WindowHostService host,
    Func<TraceViewerViewModel> viewModelFactory) : ITraceViewerChatHandoffService
{
    public void Handoff(UdsChatHandoffPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var window = host.Show(WindowKey.TraceViewer, () => new TraceViewerView(viewModelFactory()));
        if (window is null) return;

        if (window.DataContext is TraceViewerViewModel viewModel)
            viewModel.AcceptChatHandoff(payload);

        if (!window.IsVisible) window.Show();
        else window.Activate();
    }
}
