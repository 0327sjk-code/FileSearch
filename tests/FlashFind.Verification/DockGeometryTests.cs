// 文件用途：四边、不同缩放、负坐标屏幕及配置兼容性的纯几何回归。
using System.Windows;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Docking;

namespace FlashFind.Verification;

internal sealed partial class CheckSuite
{
    public void DockGeometryTests()
    {
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        foreach (Rect work in new[] { new Rect(0, 0, 1920, 1040), new Rect(-2560, -240, 2560, 1390), new Rect(0, 48, 1280, 672) })
        foreach (string edge in new[] { "left", "right", "top", "bottom" })
        {
            var screen = new DockScreen("geometry", work, true);
            foreach (double position in new[] { 0d, .26, .5, .82, 1 })
            {
                DockLayout layout = DockGeometry.Calculate(new() { Edge = edge, Position = position }, screen, scale);
                Check($"停靠工作区/DPI {scale} {work.Left} {edge} {position}", work.Contains(layout.Window) && work.Contains(layout.Peek));
                Rect bar = layout.SearchBar;
                double growingTop = layout.Above ? layout.Panel(layout.LogicalSize.Height).Bottom - DockMetrics.Thumb : layout.Panel(layout.LogicalSize.Height).Top;
                Check($"输入锚点独立于结果高度 {scale} {work.Left} {edge} {position}", Math.Abs(bar.Top - growingTop) < .001);
            }
            Point point = edge switch
            {
                "left" => new(work.Left + 1, work.Top + work.Height / 2), "right" => new(work.Right - 1, work.Top + work.Height / 2),
                "top" => new(work.Left + work.Width / 2, work.Top + 1), _ => new(work.Left + work.Width / 2, work.Bottom - 1)
            };
            Check($"拖动选择最近边 {scale} {work.Left} {edge}", DockGeometry.Nearest(point, screen, scale).Edge == edge);
        }
        DockSettings corrupt = new() { Edge = "unknown", Position = double.NaN };
        Check("损坏的浮栏配置恢复默认值", corrupt.Normalize().Edge == "right" && corrupt.Normalize().Position == .26);
        string config = System.IO.Path.Combine(output, "legacy-settings.json");
        System.IO.File.WriteAllText(config, "{\"Roots\":[\"D:\\\\\"],\"UseAllInternalDrives\":false}");
        AppSettings legacy = AppSettings.Load(config);
        Check("旧配置自动启用浮栏且保留硬盘选择", legacy.Dock.Enabled && legacy.Dock.Edge == "right" && !legacy.UseAllInternalDrives && legacy.Roots.Length == 1);
        DockSettings saved = new() { Edge = "bottom", Monitor = "test-screen", Position = .73 };
        (legacy with { Dock = saved }).Save(config);
        AppSettings restored = AppSettings.Load(config);
        Check("浮栏位置和硬盘配置可以共同持久化", restored.Dock == saved && restored.Roots.SequenceEqual(legacy.Roots));
    }
}
