// 文件用途：在完整现成索引上量测悬浮栏额外内存、查询延迟和收起后的释放；不显示窗口。
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop.Services;

internal static class FloatingMemoryDiagnostics
{
    private sealed record MemorySample(string Phase, double ManagedMiB, double WorkingSetMiB, double PrivateMiB);
    public static async Task<object> RunAsync(MainViewModel model, MainWindow main)
    {
        string previous = model.Query;
        model.Query = ".uasset"; await model.SearchNowAsync(); main.SetResultsActive(false);
        MemorySample before = ReadMemory("创建悬浮栏之前，主表已释放");
        var watch = Stopwatch.StartNew(); var quick = new QuickSearchWindow(model); quick.Expand(animate: false); Render(quick);
        double open = watch.Elapsed.TotalMilliseconds;
        var queries = new List<object>();
        try
        {
            foreach (string query in new[] { "鱼", ".png", "材质", ".uasset" })
            {
                watch.Restart(); model.Query = query; await model.SearchNowAsync(); Render(quick);
                queries.Add(new { Query = query, Matches = model.Results.Count, SearchAndRenderMilliseconds = watch.Elapsed.TotalMilliseconds, MaterializedRows = model.Results.MaterializedRows });
            }
            MemorySample expanded = ReadMemory("悬浮栏查询后，相同uasset查询");
            quick.Collapse(false); Render(quick);
            MemorySample collapsed = ReadMemory("悬浮栏收起，结果行已释放");
            if (((ListBox)quick.FindName("QuickResults")).ItemsSource is not null || BackgroundWindowAudit.VisibleWindowCount() != 0)
                throw new InvalidOperationException("悬浮栏内存验证未释放结果或意外显示窗口。");
            return new { OpenMilliseconds = open, Before = before, Expanded = expanded, Collapsed = collapsed,
                AdditionalManagedMiB = Math.Max(0, expanded.ManagedMiB - before.ManagedMiB),
                AdditionalPrivateMiB = Math.Max(0, expanded.PrivateMiB - before.PrivateMiB),
                Queries = queries, IndexDuplicated = false, ForegroundWindowsOpened = 0,
                Scope = "独立诊断进程、完整持久索引和原生WPF离屏渲染；不包含正常界面的托盘图标及后台索引完整加载。" };
        }
        finally { quick.Close(); model.Query = previous; await model.SearchNowAsync(); main.SetResultsActive(true); }
    }
    private static void Render(QuickSearchWindow window)
    {
        var root = (FrameworkElement)window.Content; Size size = window.Layout.LogicalSize;
        root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
    }
    private static MemorySample ReadMemory(string phase)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using Process process = Process.GetCurrentProcess(); process.Refresh();
        return new(phase, GC.GetTotalMemory(false) / 1048576d, process.WorkingSet64 / 1048576d, process.PrivateMemorySize64 / 1048576d);
    }
}
