// 文件用途：同一块白色表面的四边圆角裁切，避免另开弹窗的视觉效果。
using System.Windows;
using System.Windows.Media;

namespace FlashFind.Desktop.Docking;

public static class DockOutline
{
    public static CornerRadius Corners(string edge) => edge switch
    {
        "left" => new(0, DockMetrics.Corner, DockMetrics.Corner, 0),
        "top" => new(0, 0, DockMetrics.Corner, DockMetrics.Corner),
        "bottom" => new(DockMetrics.Corner, DockMetrics.Corner, 0, 0),
        _ => new(DockMetrics.Corner, 0, 0, DockMetrics.Corner)
    };
    public static Geometry Clip(Rect box, string edge)
    {
        CornerRadius corners = Corners(edge); double cap = Math.Min(box.Width, box.Height) / 2;
        double a = Math.Min(corners.TopLeft, cap), b = Math.Min(corners.TopRight, cap), c = Math.Min(corners.BottomRight, cap), d = Math.Min(corners.BottomLeft, cap);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext draw = geometry.Open())
        {
            draw.BeginFigure(new(box.Left + a, box.Top), true, true);
            draw.LineTo(new(box.Right - b, box.Top), true, false); Arc(draw, new(box.Right, box.Top + b), b);
            draw.LineTo(new(box.Right, box.Bottom - c), true, false); Arc(draw, new(box.Right - c, box.Bottom), c);
            draw.LineTo(new(box.Left + d, box.Bottom), true, false); Arc(draw, new(box.Left, box.Bottom - d), d);
            draw.LineTo(new(box.Left, box.Top + a), true, false); Arc(draw, new(box.Left + a, box.Top), a);
        }
        geometry.Freeze(); return geometry;
    }
    private static void Arc(StreamGeometryContext draw, Point end, double radius)
    { if (radius == 0) draw.LineTo(end, true, false); else draw.ArcTo(end, new(radius, radius), 0, false, SweepDirection.Clockwise, true, false); }
}
