// 文件用途：离屏复现定位按钮焦点滚动，验证点击命中、连续定位、滚轮、拖条及虚拟化。
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashFind.Core;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Docking;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop.Services;

public static class QuickLocateDiagnostics
{
    private const double GeometryTolerance = .01;
    private const int ConsecutiveClicks = 3;
    public static async Task<IReadOnlyDictionary<string, bool>> RunAsync(MainViewModel model, IReadOnlyList<FileEntry> fixtures,
        string output, Func<int> locationCount)
    {
        Directory.CreateDirectory(output);
        FileEntry[] files = fixtures.Where(entry => !entry.IsDirectory).Take(2000).ToArray();
        if (files.Length < 6) throw new ArgumentException("定位回归需要至少六个独立的真实样本文件。", nameof(fixtures));
        var checks = new Dictionary<string, bool>(); var samples = new List<object>();
        string previousQuery = model.Query; CategoryChoice previousCategory = model.Category;
        model.Category = model.Categories[0]; model.Query = "离屏定位回归"; await model.SearchNowAsync();
        try
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            foreach (int count in new[] { 1, 2, 5, 6, files.Length }.Distinct())
            {
                model.Results.Update(files.Take(count).ToArray()); model.Selected = (FileResult?)model.Results[0];
                var quick = new QuickSearchWindow(model);
                try
                {
                    quick.ApplyLayout(DockGeometry.Calculate(new DockSettings { Edge = edge, Position = .8 },
                        new DockScreen("locate-regression", new Rect(0, 0, 1920, 1040), true), 1));
                    quick.Expand(animate: false); Render(quick);
                    var root = (FrameworkElement)quick.Content; var list = (ListBox)quick.FindName("QuickResults");
                    ScrollViewer viewer = Descendants<ScrollViewer>(list).First();
                    string label = $"{edge} / {count}条";
                    using var visibility = new OffscreenVisibility(root);
                    Check(label + "：保留原生虚拟化的像素滚动", VirtualizingPanel.GetIsVirtualizing(list) &&
                        VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel && ScrollViewer.GetCanContentScroll(list));
                    if (count <= 5) Check(label + "：完整容纳结果，无多余滚动条", viewer.ScrollableHeight < GeometryTolerance &&
                        viewer.ComputedVerticalScrollBarVisibility == Visibility.Collapsed);
                    else
                    {
                        viewer.ScrollToVerticalOffset(DockMetrics.RowHeight / 2); await Settle(quick);
                        visibility.Refresh(root);
                    }
                    Button[] buttons = VisibleButtons(list, root, viewer).ToArray();
                    Check(label + "：存在完整可点击的定位按钮", buttons.Length > 0);
                    double beforeOffset = viewer.VerticalOffset;
                    int beforeLocations = locationCount();
                    var selectedButtons = new[] { buttons[0], buttons[^1] }.Distinct().ToArray();
                    foreach (Button button in selectedButtons)
                    {
                        Rect beforeBounds = Bounds(button, root); Point center = Center(beforeBounds);
                        Check(label + "：点击区域命中按钮且不与滚动条重叠 / " + beforeBounds.Top,
                            HitButton(root, center) == button && Descendants<ScrollBar>(list).Where(bar => bar.Visibility == Visibility.Visible)
                                .All(bar => !Bounds(bar, root).IntersectsWith(beforeBounds)));
                        Check(label + "：定位动作不抢输入焦点 / " + beforeBounds.Top, !button.Focusable && !button.IsTabStop);
                        for (int click = 0; click < ConsecutiveClicks; click++)
                        {
                            // 直接覆盖 WPF 在鼠标按下获取焦点后触发的相同滚动路径，不移动系统鼠标或焦点。
                            button.BringIntoView(); await Settle(quick); visibility.Refresh(root);
                            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                            await Settle(quick); visibility.Refresh(root);
                            Check(label + $"：定位{beforeBounds.Top}/{click + 1}次后按钮和偏移保持不变",
                                Math.Abs(viewer.VerticalOffset - beforeOffset) < GeometryTolerance &&
                                Close(Bounds(button, root), beforeBounds) && HitButton(root, center) == button && Mouse.Captured is null);
                        }
                    }
                    Check(label + "：每次点击只分派一次真实定位", locationCount() - beforeLocations == selectedButtons.Length * ConsecutiveClicks);
                    if (count > 5) await VerifyScrolling(quick, list, viewer, label, Check, visibility);
                    int rows = Descendants<ListBoxItem>(list).Count();
                    Check(label + "：只创建可见行和少量缓存行", rows > 0 && rows < 24);
                    Check(label + "：没有创建窗口句柄或前台窗口", new WindowInteropHelper(quick).Handle == IntPtr.Zero && BackgroundWindowAudit.VisibleWindowCount() == 0);
                    samples.Add(new { Edge = edge, Results = count, BeforeOffset = beforeOffset,
                        LocateDispatches = locationCount() - beforeLocations, MaterializedRows = rows });
                    if (count == 2) Render(quick, Path.Combine(output, edge + "_两条结果.png"));
                }
                finally { quick.Close(); }
            }
            File.WriteAllText(Path.Combine(output, "locate-regression.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Checks = checks, Cases = samples, ForegroundWindowsOpened = 0, ExplorerInvoked = false,
                Scope = "真实原生控件与实际文件的离屏布局、WPF焦点滚动路径和定位分派；滚轮和滑块使用进程内路由事件，不发送系统输入。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            return checks;
        }
        finally { model.Category = previousCategory; model.Query = previousQuery; await model.SearchNowAsync(); }
        void Check(string name, bool passed) { checks.Add(name, passed); if (!passed) throw new InvalidOperationException("定位回归失败：" + name); }
    }

    private static async Task VerifyScrolling(QuickSearchWindow quick, ListBox list, ScrollViewer viewer, string label,
        Action<string, bool> check, OffscreenVisibility visibility)
    {
        viewer.ScrollToTop(); await Settle(quick); visibility.Refresh((FrameworkElement)quick.Content);
        viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        await Settle(quick); visibility.Refresh((FrameworkElement)quick.Content);
        check(label + "：滚轮仍能正常向下滚动", viewer.VerticalOffset > GeometryTolerance);
        viewer.ScrollToTop(); await Settle(quick); visibility.Refresh((FrameworkElement)quick.Content);
        ScrollBar bar = Descendants<ScrollBar>(list).Single(item => item.Orientation == Orientation.Vertical && item.Visibility == Visibility.Visible);
        Track track = (Track)bar.Template.FindName("PART_Track", bar);
        track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, bar.ActualHeight / 2) { RoutedEvent = Thumb.DragDeltaEvent });
        track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, bar.ActualHeight / 2, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Settle(quick); visibility.Refresh((FrameworkElement)quick.Content);
        check(label + "：滚动条滑块仍能正常滚动", viewer.VerticalOffset > GeometryTolerance && Mouse.Captured is null);
    }
    private static IEnumerable<Button> VisibleButtons(ListBox list, Visual root, ScrollViewer viewer)
    {
        ScrollContentPresenter viewport = Descendants<ScrollContentPresenter>(viewer).First(); Rect bounds = Bounds(viewport, root);
        return Descendants<Button>(list).Where(button => System.Windows.Automation.AutomationProperties.GetName(button) == "便捷搜索定位文件" && bounds.Contains(Bounds(button, root)));
    }
    private static Button? HitButton(Visual root, Point point)
    {
        DependencyObject? hit = VisualTreeHelper.HitTest(root, point)?.VisualHit;
        while (hit is not null) { if (hit is Button button) return button; hit = VisualTreeHelper.GetParent(hit); }
        return null;
    }
    private static Rect Bounds(FrameworkElement element, Visual root) => element.TransformToAncestor(root)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static Point Center(Rect bounds) => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    private static bool Close(Rect left, Rect right) => Math.Abs(left.X - right.X) < GeometryTolerance && Math.Abs(left.Y - right.Y) < GeometryTolerance;
    private static async Task Settle(QuickSearchWindow quick)
    { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Render(quick); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Render(quick); }
    private static void Render(QuickSearchWindow quick, string? output = null)
    {
        var root = (FrameworkElement)quick.Content; Size size = quick.Layout.LogicalSize;
        root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout();
        if (output is null) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(output); encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private sealed class OffscreenVisibility : IDisposable
    {
        // WPF FindScrollOffset 会跳过隐藏 ItemsHost。仅翻转离屏视觉树缓存位以覆盖该分支；从不创建/显示 HWND。
        private static readonly MethodInfo WriteFlag = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("当前 WPF 不支持离屏滚动路径诊断。");
        private static readonly object VisibleFlag = Enum.Parse(WriteFlag.GetParameters()[0].ParameterType, "IsVisibleCache");
        private readonly Dictionary<UIElement, bool> _states = new();
        public OffscreenVisibility(DependencyObject root) => Refresh(root);
        public void Refresh(DependencyObject root)
        {
            foreach (UIElement visual in Descendants<UIElement>(root).Where(element => element.Visibility == Visibility.Visible))
            { _states.TryAdd(visual, visual.IsVisible); WriteFlag.Invoke(visual, new[] { VisibleFlag, (object)true }); }
        }
        public void Dispose() { foreach ((UIElement visual, bool visible) in _states) WriteFlag.Invoke(visual, new[] { VisibleFlag, (object)visible }); }
    }
}
