// 文件用途：真实 WPF 悬浮栏离屏检查；托盘流程使用替身，不显示图标、窗口或资源管理器。
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Docking;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop.Services;

public static class QuickSearchDiagnostics
{
    public static async Task<IReadOnlyDictionary<string, bool>> RunAsync(MainViewModel model, string output, Func<int> locationCount)
    {
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        var environment = new DiagnosticEnvironment();
        var main = new MainWindow(model); var tray = new DiagnosticTray(); var host = new DiagnosticHost();
        using var session = new DesktopSession(main, model, tray, host, () => new(model, environment));
        var initialDock = model.Settings.Dock; var layoutReadbacks = new List<object>();
        try
        {
            Verify("主窗口启动不提前创建悬浮窗", session.QuickWindow is null);
            session.MoveToTray(); QuickSearchWindow quick = session.QuickWindow!;
            Verify("最小化隐藏主窗并显示同一个浮栏", session.IsInTray && !host.MainShown && host.QuickShown);
            Verify("悬浮栏复用主窗口模型与索引", ReferenceEquals(quick.ViewModel, model) && ReferenceEquals(quick.DataContext, main.DataContext));
            foreach (string edge in new[] { "left", "right", "top", "bottom" })
            {
                model.Query = ""; await model.SearchNowAsync();
                DockLayout layout = DockGeometry.Calculate(new() { Edge = edge, Position = .8 }, environment.Screen, 1);
                quick.Collapse(false); quick.ApplyLayout(layout); Render(quick, Path.Combine(output, edge + "_收起.png"));
                Rect peek = quick.Layout.LocalPeek;
                Verify(edge + " 入口只露出22像素", Math.Abs((edge is "left" or "right" ? peek.Width : peek.Height) - DockMetrics.Peek) < .01);
                quick.Expand(animate: false); Render(quick);
                Verify(edge + " 空查询仅展开64像素高的搜索栏", quick.IsExpanded && quick.PanelHeight == DockMetrics.Thumb);
                TextBox input = (TextBox)quick.FindName("QuickInput"); FrameworkElement bar = (FrameworkElement)quick.FindName("SearchBar");
                Point before = bar.TransformToAncestor((Visual)quick.Content).Transform(new());
                int inputStep = 0;
                foreach (string query in new[] { "水", "水面", "水面材质_浅", "不存在的文件名", "水面", "" })
                {
                    input.Text = query; await Flush(); await model.SearchNowAsync(); await Task.Delay(210); Render(quick);
                    Point after = bar.TransformToAncestor((Visual)quick.Content).Transform(new());
                    environment.Pointer = CenterInScreen(quick.Layout.SearchBar, quick.Layout);
                    quick.PointerLeft(environment.Pointer);
                    Verify(edge + " 输入刷新保持展开和固定锚点 " + (++inputStep) + ": " + query, quick.IsExpanded && model.Query == query && Math.Abs(after.Y - before.Y) < .01 && Math.Abs(after.X - before.X) < .01);
                }
                input.Text = "水面"; await Flush(); await model.SearchNowAsync(); await Task.Delay(210); Render(quick, Path.Combine(output, edge + "_搜索结果.png"));
                ListBox results = (ListBox)quick.FindName("QuickResults");
                Verify(edge + " 全部真实文件结果接入虚拟列表", ReferenceEquals(results.ItemsSource, model.Results) && results.Items.Count == model.Results.Count);
                string? selection = model.Selected?.FullPath;
                var target = DockGeometry.Interpolate(quick.Layout.LocalPeek, quick.Layout.Panel(quick.PanelHeight), quick.Reveal);
                Point outside = new(quick.Layout.Window.X + (target.Right + DockMetrics.Margin) * quick.Layout.Scale,
                    quick.Layout.Window.Y + target.Bottom * quick.Layout.Scale);
                quick.PointerLeft(outside);
                Verify(edge + " 鼠标实际移开当次事件就开始收起", !quick.IsExpanded);
                await Task.Delay(190); Render(quick); await Flush();
                File.WriteAllText(Path.Combine(output, edge + "_收起状态.json"), JsonSerializer.Serialize(new { quick.IsExpanded, quick.Reveal,
                    RowsDetached = results.ItemsSource is null, Before = selection, After = model.Selected?.FullPath }, new JsonSerializerOptions { WriteIndented = true }));
                Verify(edge + " 收起释放结果且保留选择", results.ItemsSource is null && model.Selected?.FullPath == selection);
                quick.Expand(animate: false); Render(quick);
                layoutReadbacks.Add(new { Edge = edge, layout.Window, layout.Peek, layout.Above, SearchAnchor = before });
            }
            IReadOnlyDictionary<string, bool> placeholders = await InputDiagnostics.VerifyAsync((TextBox)quick.FindName("QuickInput"), (TextBlock)quick.FindName("QuickPlaceholder"));
            foreach ((string key, bool value) in placeholders) Verify("悬浮栏输入提示: " + key, value);
            model.Query = "水面"; await model.SearchNowAsync(); quick.Expand(animate: false); Render(quick);
            Button locate = Descendants<Button>((DependencyObject)quick.Content).First(button => System.Windows.Automation.AutomationProperties.GetName(button) == "便捷搜索定位文件");
            int locationsBefore = locationCount();
            ((IInvokeProvider)new ButtonAutomationPeer(locate).GetPattern(PatternInterface.Invoke)).Invoke(); await Flush();
            Verify("真实悬浮结果行的文件夹按钮调用定位", locationCount() == locationsBefore + 1);
            int virtualRows = Descendants<ListBoxItem>((DependencyObject)quick.FindName("QuickResults")).Count();
            Verify("结果只实例化可见行", virtualRows > 0 && virtualRows < 16);
            tray.Restore(); Verify("托盘恢复主窗口且关闭浮栏", host.MainShown && !host.QuickShown && !session.IsInTray);
            tray.Search(); Verify("托盘便捷搜索直接展开原有浮栏", session.IsInTray && host.QuickShown && quick.IsExpanded);
            DockSettings beforeToggle = model.Settings.Dock;
            tray.Toggle(); Verify("悬浮开关关闭后仍保留托盘和查询", !host.QuickShown && !model.Settings.Dock.Enabled && model.Query == "水面");
            tray.Toggle(); Verify("悬浮开关开启复用同一窗口", host.QuickShown && ReferenceEquals(quick, session.QuickWindow));
            Verify("浮栏设置不改变默认硬盘范围", model.Settings.Roots.SequenceEqual(main.ViewModel.Settings.Roots) && model.Settings.Dock.Edge == beforeToggle.Edge);
            tray.Reset(); Verify("重置默认主屏右侧并保存", model.Settings.Dock.Edge == "right" && model.Settings.Dock.Position == .26 && model.Settings.Dock.Monitor == "");
            int updateRequests = 0; session.CheckUpdatesRequested += () => updateRequests++;
            tray.CheckUpdates(); Verify("托盘保留手动更新入口", updateRequests == 1);
            tray.Exit(); Verify("托盘退出进入应用退出流程", host.ExitRequested);
            Verify("诊断没有创建可见窗口", BackgroundWindowAudit.VisibleWindowCount() == 0);
            using (var resource = new EmbeddedTrayIcon()) Verify("内嵌图标可以创建真实托盘HICON资源", resource.Handle != IntPtr.Zero);
            var native = new QuickSearchWindow(model);
            try
            {
                IntPtr handle = new WindowInteropHelper(native).EnsureHandle();
                foreach (string edge in new[] { "left", "right", "top", "bottom" })
                {
                    native.PlaceNative(new() { Edge = edge, Position = .4 }); await Flush();
                    DockNative.GetWindowRect(handle, out DockNative.NativeRect actual);
                    Rect rectangle = actual.ToRect(), expected = native.Layout.Window;
                    Verify(edge + " 原生隐藏HWND按实际DPI落在工作区", Math.Abs(rectangle.X - expected.X) <= 1 && Math.Abs(rectangle.Y - expected.Y) <= 1 &&
                        Math.Abs(rectangle.Width - expected.Width) <= 1 && Math.Abs(rectangle.Height - expected.Height) <= 1 && !native.IsVisible);
                }
                Verify("原生句柄验证没有显示窗口", BackgroundWindowAudit.VisibleWindowCount() == 0);
            }
            finally { native.Close(); }
            object report = new { Passed = true, Checks = checks, Layouts = layoutReadbacks,
                AdditionalManagedWindows = 1, SharedIndex = true, BrowserProcesses = 0,
                ForegroundWindowsOpened = 0, ExplorerInvoked = false, NativeNotificationIconInstalled = false,
                VerificationScope = "真实WPF控件离屏渲染、实际隐藏HWND/DPI定位和真实文件搜索、PIDL定位分派；托盘菜单使用替身，未操作用户桌面或系统输入法。" };
            File.WriteAllText(Path.Combine(output, "floating-verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return checks;
        }
        finally { model.SaveDockSettings(initialDock); main.Close(); }

        void Verify(string name, bool passed) { checks.Add(name, passed); if (!passed) throw new InvalidOperationException("悬浮栏验证失败：" + name); }
    }
    private static Point CenterInScreen(Rect local, DockLayout layout) => new(layout.Window.X + (local.X + local.Width / 2) * layout.Scale, layout.Window.Y + (local.Y + local.Height / 2) * layout.Scale);
    private static async Task Flush() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Render(QuickSearchWindow window, string? output = null)
    {
        var root = (FrameworkElement)window.Content; Size size = window.Layout.LogicalSize;
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
    private sealed class DiagnosticEnvironment : IDockEnvironment
    {
        public DockScreen Screen { get; } = new("diagnostic", new(0, 0, 1920, 1040), true);
        public Point Pointer { get; set; }
        public Point Cursor => Pointer;
        public DockScreen Resolve(string id) => Screen;
        public DockScreen At(Point pointer) => Screen;
    }
    private sealed class DiagnosticHost : IDesktopWindowHost
    {
        public bool MainShown { get; private set; } = true;
        public bool QuickShown { get; private set; }
        public bool ExitRequested { get; private set; }
        public void ShowMain(MainWindow window) => MainShown = true;
        public void HideMain(MainWindow window) => MainShown = false;
        public void ShowQuick(QuickSearchWindow window) => QuickShown = true;
        public void HideQuick(QuickSearchWindow window) => QuickShown = false;
        public void Exit(MainWindow window) => ExitRequested = true;
    }
    private sealed class DiagnosticTray : INotificationArea
    {
        public event Action? RestoreRequested, SearchRequested, ToggleDockRequested, ResetDockRequested, ExitRequested, CheckUpdatesRequested;
        public bool DockEnabled { get; set; }
        public void Restore() => RestoreRequested?.Invoke();
        public void Search() => SearchRequested?.Invoke();
        public void Toggle() => ToggleDockRequested?.Invoke();
        public void Reset() => ResetDockRequested?.Invoke();
        public void Exit() => ExitRequested?.Invoke();
        public void CheckUpdates() => CheckUpdatesRequested?.Invoke();
        public void NotifyUpdate(string message) { }
        public void Dispose() { }
    }
}
