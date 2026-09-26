using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels.Xcp;
using ScottPlot;
using ScottPlot.WPF;

namespace PeakCan.Host.App.Views.Xcp;

/// <summary>
/// S6-T4 MAP 只读可视化页视图（spec D4）。渲染分层与回放页同款：
/// VM 持数据与状态，本 code-behind 持 WpfPlot 直操。只读——本页无任何写路径（S1 钉）。
/// </summary>
public partial class XcpMapView : UserControl
{
    public XcpMapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private XcpMapPanelViewModel? Vm => DataContext as XcpMapPanelViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is XcpMapPanelViewModel oldVm)
            oldVm.RenderRequested -= OnRenderRequested;
        if (e.NewValue is XcpMapPanelViewModel vm)
            vm.RenderRequested += OnRenderRequested;
    }

    private void OnRenderRequested()
    {
        // VM 渲染请求可能来自后台加载路径；渲染统一调度回 UI 线程。
        Dispatcher.BeginInvoke(Render);
    }

    private void Render()
    {
        var vm = Vm;
        var result = vm?.Result;
        var plot = PlotControl.Plot;
        plot.Clear();

        if (result is { } data)
        {
            var hm = plot.Add.Heatmap(data.Grid);
            _ = hm;
            plot.Axes.SetLimitsX(0, data.Grid.GetLength(1));
            plot.Axes.SetLimitsY(0, data.Grid.GetLength(0));
        }

        PlotControl.Refresh();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        var result = Vm?.Result;
        if (result is null || sender is not WpfPlot pv)
            return;

        try
        {
            // WPF DIP → 设备像素（TraceViewer 同款换算）。
            var posDip = e.GetPosition(pv);
            var dpiScale = PresentationSource.FromVisual(pv)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var pixel = new ScottPlot.Pixel((float)(posDip.X * dpiScale), (float)(posDip.Y * dpiScale));
            var coordinates = pv.Plot.GetCoordinates(pixel);
            var x = (int)Math.Floor(coordinates.X);
            var y = (int)Math.Floor(coordinates.Y);
            if (x < 0 || y < 0 || y >= result.Grid.GetLength(0) || x >= result.Grid.GetLength(1))
            {
                ReadoutText.Text = "（悬停读取格值）";
                return;
            }
            var value = result.Grid[y, x];
            ReadoutText.Text = double.IsNaN(value)
                ? $"[{x},{y}] 无数据"
                : $"[{x},{y}] = {value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}"
                  + (result.Unit is { } u ? $" {u}" : string.Empty);
        }
        catch
        {
            // 图表未就绪，忽略（TraceViewer 同款口径）。
        }
    }
}
