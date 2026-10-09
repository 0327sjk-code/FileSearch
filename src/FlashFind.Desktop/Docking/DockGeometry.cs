// 文件用途：物理像素中的四边吸附与 DPI 几何；输入栏锚点不依赖搜索结果高度。
using System.Windows;
using FlashFind.Core.Storage;

namespace FlashFind.Desktop.Docking;

public static class DockMetrics
{
    public const double Peek = 22, Thumb = 64, PanelWidth = 416, MaximumPanelHeight = 460;
    public const double Margin = 8, Corner = 16, DragThreshold = 4, DragWidth = 40;
    public const double RowHeight = 62, ToolbarHeight = 48, FooterHeight = 38, EmptyHeight = 124;
    public const double OpenMilliseconds = 220, CloseMilliseconds = 150, ResizeMilliseconds = 180;
    public const double SnapMilliseconds = 160, FramesPerSecond = 60;
    public const double PointerCheckMilliseconds = 32;
}

public sealed record DockScreen(string Id, Rect WorkArea, bool Primary);
public sealed record DockLayout(DockSettings Placement, Rect Window, Rect Peek, double Scale, bool Above)
{
    public Size LogicalSize => new(Window.Width / Scale, Window.Height / Scale);
    public Rect LocalPeek => new((Peek.X - Window.X) / Scale, (Peek.Y - Window.Y) / Scale, Peek.Width / Scale, Peek.Height / Scale);
    public Rect Panel(double height) => new(0, Above ? LogicalSize.Height - height : 0, LogicalSize.Width, height);
    public Rect SearchBar => Panel(DockMetrics.Thumb);
}

public static class DockGeometry
{
    public static DockLayout Calculate(DockSettings saved, DockScreen screen, double scale)
    {
        DockSettings dock = saved.Normalize(); Rect area = screen.WorkArea;
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        bool vertical = dock.Edge is "left" or "right";
        double thumb = DockMetrics.Thumb * scale, peek = DockMetrics.Peek * scale, margin = DockMetrics.Margin * scale;
        double axis = vertical ? area.Height : area.Width;
        double inset = Math.Min(thumb / 2 + margin, axis / 2);
        double center = inset + dock.Position * Math.Max(axis - inset * 2, 0);
        Rect entry = vertical
            ? new(dock.Edge == "right" ? area.Right - peek : area.Left, area.Top + center - thumb / 2, peek, thumb)
            : new(area.Left + center - thumb / 2, dock.Edge == "bottom" ? area.Bottom - peek : area.Top, thumb, peek);
        bool above = dock.Edge == "bottom" || (vertical && dock.Position > .5);
        double available = vertical ? above ? entry.Bottom - area.Top : area.Bottom - entry.Top : area.Height;
        double width = Math.Min(DockMetrics.PanelWidth * scale, area.Width - margin * 2);
        double height = Math.Min(DockMetrics.MaximumPanelHeight * scale, Math.Max(thumb, available - margin));
        double x = vertical ? dock.Edge == "right" ? area.Right - width : area.Left
            : Math.Clamp(entry.X + thumb / 2 - width / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - width - margin));
        double y = vertical ? above ? entry.Bottom - height : entry.Top : above ? area.Bottom - height : area.Top;
        return new(dock, new(x, y, width, height), entry, scale, above);
    }

    public static DockSettings Nearest(Point pointer, DockScreen screen, double scale, bool enabled = true)
    {
        Rect area = screen.WorkArea;
        var distances = new[] { ("left", Math.Abs(pointer.X - area.Left)), ("right", Math.Abs(area.Right - pointer.X)),
            ("top", Math.Abs(pointer.Y - area.Top)), ("bottom", Math.Abs(area.Bottom - pointer.Y)) };
        string edge = distances.MinBy(item => item.Item2).Item1;
        bool vertical = edge is "left" or "right";
        double length = vertical ? area.Height : area.Width;
        double coordinate = vertical ? pointer.Y - area.Top : pointer.X - area.Left;
        double inset = Math.Min((DockMetrics.Thumb / 2 + DockMetrics.Margin) * scale, length / 2);
        return new() { Edge = edge, Position = Math.Clamp((coordinate - inset) / Math.Max(length - inset * 2, 1), 0, 1), Monitor = screen.Id, Enabled = enabled };
    }

    public static Rect Interpolate(Rect start, Rect end, double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return new(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t,
            start.Width + (end.Width - start.Width) * t, start.Height + (end.Height - start.Height) * t);
    }
}
