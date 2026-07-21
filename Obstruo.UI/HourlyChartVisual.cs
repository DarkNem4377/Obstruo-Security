using System.Windows;
using System.Windows.Media;

namespace Obstruo.UI;

/// <summary>
/// DrawingVisual host for the 24-hour activity bar chart.
/// </summary>
public sealed class HourlyChartVisual : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private int[] _data = Array.Empty<int>();

    public HourlyChartVisual()
    {
        AddVisualChild(_visual);
        AddLogicalChild(_visual);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    /// <summary>
    /// Pass 24 integers (index 0–23 = hour 0–23).
    /// Safe to call before or after layout — OnRenderSizeChanged handles the late case.
    /// </summary>
    public void Draw(int[] hourlyData)
    {
        _data = hourlyData ?? Array.Empty<int>();
        Redraw();
    }

    private void Redraw()
    {
        if (ActualWidth == 0 || ActualHeight == 0)
            return;

        using var dc = _visual.RenderOpen();

        double w = ActualWidth;
        double h = ActualHeight;

        // Background — matches the card color
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(0x04, 0x0C, 0x18)),
            null,
            new Rect(0, 0, w, h));

        if (_data.Length == 0)
            return;

        int count = _data.Length;
        double maxVal = _data.Max();
        if (maxVal < 1) maxVal = 1;

        double barSlot = w / count;
        double gap = barSlot * 0.30;
        double barW = barSlot - gap;
        double maxBarH = h * 0.82;
        double baselineY = h - 4.0;

        // Subtle baseline rule
        var baselinePen = new Pen(
            new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x2C)), 0.5);
        dc.DrawLine(baselinePen,
            new Point(0, baselineY),
            new Point(w, baselineY));

        var barBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x46, 0xD1));
        var highlightBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xAA, 0x7C));
        int currentHour = DateTime.Now.Hour;

        for (int i = 0; i < count; i++)
        {
            double barH = (_data[i] / maxVal) * maxBarH;
            if (barH < 1.0) barH = 1.0; // always show a minimum sliver

            double x = i * barSlot + gap / 2.0;
            double y = baselineY - barH;
            var brush = i == currentHour ? highlightBrush : barBrush;

            dc.DrawRoundedRectangle(brush, null,
                new Rect(x, y, barW, barH), 1.5, 1.5);
        }
    }
}