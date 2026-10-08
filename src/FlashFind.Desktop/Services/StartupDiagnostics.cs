// 文件用途：独立发布包的离屏启动验证；直接查询完整缓存并取消后台扫描。
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FlashFind.Core.Indexing;
using FlashFind.Core.Storage;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop.Services;

internal static class StartupDiagnostics
{
    public static async Task RunAsync(string cachePath, string output, bool completeBackgroundLoad = false)
    {
        Directory.CreateDirectory(output);
        DateTime processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var cache = new CacheStore(cachePath); ReadyIndexInfo info = cache.ReadReadyInfo();
        var log = new AppLog(Path.Combine(output, "logs"));
        var index = new IndexService(cache, log, info.Excluded, preferNtfs: false);
        using var cancellation = new CancellationTokenSource();
        double readyMilliseconds = 0;
        index.Changed += _ =>
        {
            if (cancellation.IsCancellationRequested) return;
            if (index.Catalog.IsPersistent)
            {
                readyMilliseconds = Elapsed(processStart);
                if (!completeBackgroundLoad) cancellation.Cancel();
            }
            else if (readyMilliseconds > 0 && index.Snapshot.Length > 0) cancellation.Cancel();
        };
        var settings = new AppSettings { UseAllInternalDrives = false, Roots = info.Roots };
        var model = new MainViewModel(index, new SilentActions(), log, settings, Path.Combine(output, "settings.json"),
            Dispatcher.CurrentDispatcher, () => info.Roots.Select(root => new IndexedDrive(root, "", "NTFS", 17, true, false)).ToArray());
        var window = new MainWindow(model);
        try
        {
            Task startup = model.StartAsync(cancellation.Token);
            await UntilAsync(() => model.Results.Count == info.FileCount);
            Render(window);
            bool earlyResultsBeforeHydration = index.Snapshot.Length == 0 && index.Catalog.IsPersistent;
            double firstRendered = Elapsed(processStart);
            var measurements = new List<object> { Memory("完整文件列表首次显示") };
            var queries = new List<object>();
            foreach (string query in new[] { "鱼", "材质", ".uasset", "UnrealEditor", "Epic", "不存在的文件", "" })
            {
                var watch = Stopwatch.StartNew(); double resultsAssigned = 0, selectionAssigned = 0;
                System.ComponentModel.PropertyChangedEventHandler monitor = (_, args) =>
                {
                    if (args.PropertyName == nameof(model.Results)) resultsAssigned = watch.Elapsed.TotalMilliseconds;
                    if (args.PropertyName == nameof(model.Selected)) selectionAssigned = watch.Elapsed.TotalMilliseconds;
                };
                model.PropertyChanged += monitor;
                model.Query = query; await model.SearchNowAsync();
                double searchAndBinding = watch.Elapsed.TotalMilliseconds; Render(window);
                model.PropertyChanged -= monitor;
                queries.Add(new { Query = query, Matches = model.Results.Count, SearchAndRenderMilliseconds = watch.Elapsed.TotalMilliseconds,
                    SearchAndBindingMilliseconds = searchAndBinding, ResultsAssignedMilliseconds = resultsAssigned,
                    SelectionAssignedMilliseconds = selectionAssigned,
                    MaterializedRows = model.Results.MaterializedRows,
                    EngineTiming = model.Timing, FirstResultHasPath = model.Selected is null || Path.IsPathFullyQualified(model.Selected.FullPath) });
            }
            measurements.Add(Memory("多次查询后"));
            int visibleWindows = BackgroundWindowAudit.VisibleWindowCount();
            if (visibleWindows != 0 || !earlyResultsBeforeHydration || index.State.IsScanning)
                throw new InvalidOperationException("启动验证进入了正常界面、完整加载或磁盘扫描。");
            try { await startup; } catch (OperationCanceledException) { }
            if (completeBackgroundLoad)
            {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await model.SearchNowAsync(); Render(window);
                if (index.Snapshot.Length != info.EntryCount || index.State.IsScanning || model.Results.Count != info.FileCount)
                    throw new InvalidOperationException("后台加载后文件结果数量不一致，或意外进入扫描。");
                measurements.Add(Memory("后台完整加载结束"));
            }
            object report = new { Version = typeof(MainViewModel).Assembly.GetName().Version!.ToString(),
                Entries = info.EntryCount, Files = info.FileCount, ProcessToSearchableMilliseconds = readyMilliseconds,
                ProcessToFirstRenderedMilliseconds = firstRendered, Measurements = measurements, Queries = queries,
                FullResultsAvailableBeforeHydration = earlyResultsBeforeHydration, BackgroundLoadCompleted = completeBackgroundLoad,
                ForegroundWindowsOpened = visibleWindows, ExplorerInvoked = false,
                UserDiskScanStarted = false };
            File.WriteAllText(Path.Combine(output, "native-startup.json"), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await model.DisposeAsync(); window.Close(); }
    }

    private static double Elapsed(DateTime processStart) => (DateTime.UtcNow - processStart).TotalMilliseconds;
    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        { if (watch.Elapsed.TotalSeconds > 5) throw new TimeoutException("完整缓存没有及时显示。"); await Task.Delay(10); }
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Render(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1140, 748)); content.Arrange(new Rect(0, 0, 1140, 748)); content.UpdateLayout();
    }
    private static object Memory(string phase)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using Process process = Process.GetCurrentProcess(); process.Refresh();
        return new { Phase = phase, ManagedMiB = GC.GetTotalMemory(false) / 1048576d, WorkingSetMiB = process.WorkingSet64 / 1048576d,
            PrivateMiB = process.PrivateMemorySize64 / 1048576d };
    }
    private sealed class SilentActions : IFileActions
    {
        public void Open(FlashFind.Core.FileEntry entry) { }
        public void Locate(FlashFind.Core.FileEntry entry) { }
        public void CopyPath(FlashFind.Core.FileEntry entry) { }
    }
}
