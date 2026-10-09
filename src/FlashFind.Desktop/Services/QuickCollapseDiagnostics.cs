// 文件用途：真实 WPF 浮栏和 DispatcherTimer 的离屏回归；不显示窗口，不操作系统鼠标、键盘或输入法。
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FlashFind.Desktop.Docking;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop.Services;

public static class QuickCollapseDiagnostics
{
    private const int MonitorObservationMilliseconds = 100;
    private const int CheckTimeoutMilliseconds = 1500;
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task<IReadOnlyDictionary<string, bool>> RunAsync(MainViewModel model, string output)
    {
        Directory.CreateDirectory(output);
        string previousQuery = model.Query;
        CategoryChoice previousCategory = model.Category;
        var checks = new Dictionary<string, bool>();
        var samples = new List<object>();
        model.Category = model.Categories[0]; model.Query = ""; await model.SearchNowAsync();
        try
        {
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            foreach (double position in new[] { .25, .75 })
            foreach (double scale in new[] { 1d, 1.5d, 2d })
            {
                var environment = new TestEnvironment();
                var quick = new QuickSearchWindow(model, environment);
                var visibility = new OffscreenVisibility(quick);
                string label = $"{edge}/{position}/{scale}";
                Point anchor = new();
                try
                {
                    quick.ApplyLayout(DockGeometry.Calculate(new() { Edge = edge, Position = position }, environment.Screen, scale));
                    anchor = Center(quick.Layout.SearchBar, quick.Layout);
                    Point outside = Physical(new(quick.Layout.LogicalSize.Width / 2,
                        quick.Layout.Above ? quick.Layout.SearchBar.Top - DockMetrics.Margin : quick.Layout.SearchBar.Bottom + DockMetrics.Margin), quick.Layout);
                    Check("透明窗口内而搜索栏外", quick.Layout.Window.Contains(outside) && !quick.Layout.SearchBar.Contains(Local(outside, quick.Layout)));
                    Check("未显示和收起时不监测", !quick.IsVisible && !quick.IsPointerMonitorRunning);
                    visibility.Set(true);

                    Open();
                    await Until(() => environment.CursorReads > 0);
                    Check("展开可见时启动监测", quick.IsExpanded && quick.IsPointerMonitorRunning);
                    environment.Pointer = outside;
                    var watch = Stopwatch.StartNew();
                    await Until(() => !quick.IsExpanded);
                    double missedLeaveMilliseconds = watch.Elapsed.TotalMilliseconds;
                    Check("缺少MouseLeave也自动开始收起", !quick.IsExpanded && !quick.IsPointerMonitorRunning);
                    await Until(() => quick.Reveal == 0);
                    Check("原有收起动画结束且释放结果", ((ListBox)quick.FindName("QuickResults")).ItemsSource is null);

                    Open();
                    quick.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    Check("内部坐标的提前离开事件不打断搜索", quick.IsExpanded);
                    environment.Pointer = outside;
                    quick.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                    Check("随后移动立即补查，不依赖第二次MouseLeave", !quick.IsExpanded);

                    Open();
                    TextBox input = (TextBox)quick.FindName("QuickInput");
                    var composition = new TextComposition(InputManager.Current, input, "");
                    input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                    environment.Pointer = outside;
                    await Task.Delay(MonitorObservationMilliseconds);
                    Check("输入法候选操作期间保留展开", quick.IsExpanded);
                    input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                    await Until(() => !quick.IsExpanded);
                    Check("输入法结束自动补查并收起", !quick.IsPointerMonitorRunning);

                    Open();
                    input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                    environment.Pointer = outside;
                    input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, input, null)
                    { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                    await Until(() => !quick.IsExpanded);
                    Check("输入框失去焦点清理残留输入法状态", !quick.IsPointerMonitorRunning);

                    Open();
                    input.Text = "水"; await model.SearchNowAsync();
                    await Task.Delay(MonitorObservationMilliseconds);
                    input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                    input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                    await Flush();
                    Check("栏内打字和输入法提交保持展开及固定锚点", quick.IsExpanded && quick.IsPointerMonitorRunning &&
                        quick.Layout.SearchBar.Contains(Local(anchor, quick.Layout)));
                    input.Text = ""; await model.SearchNowAsync();

                    Open();
                    object controller = typeof(QuickSearchWindow).GetField("_dragController", PrivateInstance)!.GetValue(quick)!;
                    controller.GetType().GetField("_tracking", PrivateInstance)!.SetValue(controller, true);
                    environment.Pointer = outside;
                    await Task.Delay(MonitorObservationMilliseconds);
                    Check("按钮按下的捕获阶段不误收起", quick.IsExpanded && Mouse.Captured is null);
                    quick.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = Mouse.LostMouseCaptureEvent });
                    await Until(() => !quick.IsExpanded);
                    Check("捕获丢失后主动补查并收起", !quick.IsPointerMonitorRunning);

                    Open();
                    controller.GetType().GetField("_tracking", PrivateInstance)!.SetValue(controller, true);
                    environment.Pointer = outside;
                    controller.GetType().GetMethod("Finish", PrivateInstance)!.Invoke(controller, new object[] { true });
                    await Until(() => !quick.IsExpanded);
                    Check("栏外释放拖动按钮不重新弹开", !quick.IsExpanded && Mouse.Captured is null);

                    Open();
                    typeof(QuickSearchWindow).GetMethod("StartDrag", PrivateInstance)!.Invoke(quick, null);
                    Check("拖动开始停止边界监测", !quick.IsExpanded && !quick.IsPointerMonitorRunning);
                    quick.SnapTo(outside); await Flush();
                    Check("拖动吸附结束保持收起", !quick.IsExpanded && !quick.IsPointerMonitorRunning);

                    quick.ApplyLayout(DockGeometry.Calculate(new() { Edge = edge, Position = position }, environment.Screen, scale));
                    Open();
                    visibility.Set(false); RefreshMonitor(quick);
                    Check("隐藏后停止监测", !quick.IsPointerMonitorRunning);
                    visibility.Set(true); RefreshMonitor(quick);
                    Check("再次显示恢复展开状态的监测", quick.IsPointerMonitorRunning);
                    quick.Collapse(false); await Flush();
                    environment.CursorReads = 0;
                    await Task.Delay(MonitorObservationMilliseconds);
                    Check("收起后没有鼠标轮询", environment.CursorReads == 0);

                    visibility.Set(false);
                    environment.Pointer = outside;
                    // IsVisible=false 保证此处不会调用 Activate/Focus；仅覆盖主动打开的托盘状态。
                    quick.Expand(focus: true, animate: false);
                    visibility.Set(true); RefreshMonitor(quick);
                    await Task.Delay(MonitorObservationMilliseconds);
                    Check("托盘主动打开时不因鼠标尚在栏外而关闭", quick.IsExpanded);
                    environment.Pointer = anchor;
                    // 等待真实监测进入，避免固定延时在后台构建负载下错过一次定时器。
                    await Until(() => !(bool)typeof(QuickSearchWindow).GetField("_awaitingPointerEntry", PrivateInstance)!.GetValue(quick)!);
                    environment.Pointer = outside;
                    await Until(() => !quick.IsExpanded);
                    Check("托盘打开后首次进入再离开即可收起", !quick.IsPointerMonitorRunning);
                    Check("没有创建HWND或操作前台", new WindowInteropHelper(quick).Handle == IntPtr.Zero &&
                        BackgroundWindowAudit.VisibleWindowCount() == 0 && Mouse.Captured is null);
                    samples.Add(new { Edge = edge, Position = position, Scale = scale, MissingMouseLeaveToCollapseMilliseconds = missedLeaveMilliseconds });
                }
                finally { visibility.Dispose(); quick.Close(); }
                Check("关闭窗口后停止并释放监测", !quick.IsPointerMonitorRunning);

                void Open() { environment.Pointer = anchor; environment.CursorReads = 0; quick.Expand(animate: false); }
                void Check(string name, bool passed)
                {
                    string key = label + "：" + name; checks.Add(key, passed);
                    if (!passed) throw new InvalidOperationException("浮栏收起回归失败：" + key);
                }
            }
            File.WriteAllText(Path.Combine(output, "collapse-regression.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Checks = checks, Samples = samples, PointerCheckMilliseconds = DockMetrics.PointerCheckMilliseconds,
                ForegroundWindowsOpened = BackgroundWindowAudit.VisibleWindowCount(), SystemInputInvoked = false,
                Scope = "真实WPF控件、真实DispatcherTimer和注入的自有控件事件；未操作用户桌面，未驱动系统输入法。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            return checks;
        }
        finally { model.Category = previousCategory; model.Query = previousQuery; await model.SearchNowAsync(); }
    }

    private static Point Center(Rect box, DockLayout layout) => Physical(new(box.X + box.Width / 2, box.Y + box.Height / 2), layout);
    private static Point Physical(Point point, DockLayout layout) => new(layout.Window.X + point.X * layout.Scale, layout.Window.Y + point.Y * layout.Scale);
    private static Point Local(Point point, DockLayout layout) => new((point.X - layout.Window.X) / layout.Scale, (point.Y - layout.Window.Y) / layout.Scale);
    private static void RefreshMonitor(QuickSearchWindow quick) => typeof(QuickSearchWindow).GetMethod("UpdatePointerMonitor", PrivateInstance)!.Invoke(quick, null);
    private static async Task Flush() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static async Task Until(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition())
        {
            if (time.ElapsedMilliseconds > CheckTimeoutMilliseconds) throw new TimeoutException("浮栏收起状态没有及时更新。");
            await Task.Delay(5);
        }
    }
    private sealed class TestEnvironment : IDockEnvironment
    {
        public DockScreen Screen { get; } = new("collapse-regression", new(0, 0, 3840, 2160), true);
        public Point Pointer { get; set; }
        public int CursorReads { get; set; }
        public Point Cursor { get { CursorReads++; return Pointer; } }
        public DockScreen Resolve(string id) => Screen;
        public DockScreen At(Point pointer) => Screen;
    }
    private sealed class OffscreenVisibility : IDisposable
    {
        private readonly QuickSearchWindow _window;
        private readonly MethodInfo _writeFlag;
        private readonly object _visibleFlag;
        public OffscreenVisibility(QuickSearchWindow window)
        {
            _window = window;
            _writeFlag = typeof(UIElement).GetMethod("WriteFlag", PrivateInstance)!;
            _visibleFlag = Enum.Parse(_writeFlag.GetParameters()[0].ParameterType, "IsVisibleCache");
        }
        public void Set(bool visible) => _writeFlag.Invoke(_window, new[] { _visibleFlag, (object)visible });
        public void Dispose() => Set(false);
    }
}
