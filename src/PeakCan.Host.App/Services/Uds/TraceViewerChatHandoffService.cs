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
        {
            // spec §14.3：已有未发送草稿时先确认；拒绝则保留原文。
            viewModel.ConfirmReplaceDraft ??= _ =>
                System.Windows.MessageBox.Show(
                    "AI 输入框里有未发送的草稿。\n\n要替换为本次 UDS 分析问题吗？",
                    "在 AI 中解读", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question)
                == System.Windows.MessageBoxResult.Yes;

            viewModel.AcceptChatHandoff(payload);
        }

        if (!window.IsVisible) window.Show();
        else window.Activate();
    }
}
