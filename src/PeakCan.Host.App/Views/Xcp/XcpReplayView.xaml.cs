using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PeakCan.Host.App.ViewModels.Xcp;
using ScottPlot;

namespace PeakCan.Host.App.Views.Xcp;

/// <summary>
/// S6-T3 回放页视图（spec D2/D3/D7）。渲染分层与 TraceViewer 同款：
/// VM 持数据与状态，本 code-behind 持 WpfPlot 直操（VM 不持 Dispatcher / 图表对象）。
/// </summary>
public partial class XcpReplayView : UserControl
{
    public XcpReplayView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private XcpReplayPanelViewModel? Vm => DataContext as XcpReplayPanelViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is XcpReplayPanelViewModel oldVm)
            oldVm.RenderRequested -= OnRenderRequested;
        if (e.NewValue is XcpReplayPanelViewModel vm)
        {
            vm.RenderRequested += OnRenderRequested;
            Render(vm); // DataContext 就位时已加载过文件 → 立即补画
        }
    }

    private void OnRenderRequested()
    {
        // VM 渲染请求可能来自非 UI 线程加载路径；渲染统一调度回 UI 线程。
        Dispatcher.BeginInvoke(new Action(Render));
    }

    private void OnLoadFileClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "加载 XCP 记录文件",
            Filter = "MDF4 记录 (*.mf4)|*.mf4|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
            Vm.LoadFile(dialog.FileName);
    }

    private void Render()
    {
        if (Vm is { } vm)
            Render(vm);
    }

    private void Render(XcpReplayPanelViewModel vm)
    {
        var plot = PlotControl.Plot;
        plot.Clear();

        var result = vm.Result;
        if (result is not null)
        {
            // 空窗画成空窗（S1 A1）：Invalid 行值 NaN → ScottPlot 自动断线。
            foreach (var ch in result.Channels)
            {
                if (vm.Channels.FirstOrDefault(c => c.Name == ch.ObjectName)?.IsSelected != true)
                    continue;
                var values = ch.Values.ToArray();
                for (var i = 0; i < values.Length; i++)
                {
                    if (ch.Invalid[i])
                        values[i] = double.NaN;
                }
                var scatter = plot.Add.Scatter(ch.Times.ToArray(), values);
                scatter.LegendText = ch.Metadata?.Unit is { } unit ? $"{ch.ObjectName} ({unit})" : ch.ObjectName;
            }

            foreach (var mark in result.GapMarks)
            {
                var line = plot.Add.VerticalLine(mark.Time);
                line.Color = Colors.OrangeRed;
                line.LineWidth = 1.5f;
            }

            if (result.Channels.Count > 0)
                plot.Axes.AutoScale();
        }

        PlotControl.Refresh();
    }
}
