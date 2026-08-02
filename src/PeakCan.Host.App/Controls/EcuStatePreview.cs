using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PeakCan.Host.App.ViewModels.EcuSimulator;

namespace PeakCan.Host.App.Controls;

/// <summary>
/// 只读 ECU 状态机预览：按 States 顺序横排圆角矩形, 从 FromState 到 ToState 画条件箭头
/// （标 ServiceId hex）。数据来自 <see cref="EditableEcuScript"/>, 编辑变化时重绘。
/// </summary>
public sealed class EcuStatePreview : Canvas
{
    private const double NodeW = 120, NodeH = 40, Gap = 60, Top = 20;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(EditableEcuScript), typeof(EcuStatePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public EditableEcuScript? Data
    {
        get => (EditableEcuScript?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (EcuStatePreview)d;
        if (e.OldValue is EditableEcuScript old) old.Changed -= c.Redraw;
        if (e.NewValue is EditableEcuScript n) n.Changed += c.Redraw;
        c.Redraw();
    }

    private void Redraw() { InvalidateMeasure(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Data is not { States.Count: > 0 }) return;

        var positions = new Dictionary<string, Point>();
        double x = 10;
        foreach (var s in Data.States)
        {
            positions[s.Name] = new Point(x, Top);
            x += NodeW + Gap;
        }

        // 箭头: wildcard FromState(null) 不画入向箭头, 只画 ToState 出向
        foreach (var s in Data.States)
        foreach (var t in s.Transitions)
        {
            if (t.ToState is not null && positions.TryGetValue(t.ToState, out var to))
            {
                var from = positions[s.Name];
                var p1 = new Point(from.X + NodeW, from.Y + NodeH / 2);
                var p2 = new Point(to.X, to.Y + NodeH / 2);
                dc.DrawLine(new Pen(Brushes.Gray, 1), p1, p2);
                dc.DrawText(new FormattedText(
                    $"{t.ServiceIdHex}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 10, Brushes.DimGray),
                    new Point((p1.X + p2.X) / 2 - 20, (p1.Y + p2.Y) / 2 - 10));
            }
        }

        foreach (var s in Data.States)
        {
            var p = positions[s.Name];
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.DarkSlateGray, 1.5),
                new Rect(p.X, p.Y, NodeW, NodeH), 6, 6);
            dc.DrawText(new FormattedText(s.Name,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Black),
                new Point(p.X + 8, p.Y + 11));
        }
    }
}
