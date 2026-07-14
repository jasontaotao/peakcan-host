// TraceViewerViewModel/Recording.partial.cs — v3.49.0 MINOR T4
// Q2: 9th partial on TraceViewerViewModel. RecordingViewModel 公开属性由
// TraceViewerViewModel.cs 主 partial ctor 末尾赋值。
//
// TraceViewerView.xaml 通过 {Binding RecordingViewModel} 把 RecordView
// UserControl 嵌到 TraceViewer 窗口内的折叠 Expander。

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    /// <summary>
    /// 由 DI 注入的 RecordViewModel singleton。TraceViewerView.xaml 把
    /// RecordView UserControl 嵌到 Recording Expander 时，把 DataContext 绑定到这里。
    /// null-tolerant（默认 ctor 调用）— 测试用 ctor 不传 RecordViewModel 时
    /// 留 null，XAML 绑定会自动 null-coalesce。
    /// </summary>
    public RecordViewModel? RecordingViewModel { get; }
}
