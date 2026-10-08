// 文件用途：后台测试入口；不显示窗口，不调用资源管理器或用户文件的关联程序。
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FlashFind.Desktop.Services;

namespace FlashFind.Verification;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--index") return LiveIndex.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--benchmark-cache") return LiveIndex.BenchmarkCache(args[1], args[2]);
        string output = args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "verification");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/文件搜索;component/Views/Theme.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        int exit = 0;
        Task run = RunAsync(output);
        _ = run.ContinueWith(task =>
        {
            if (task.IsFaulted) { Console.Error.WriteLine(task.Exception); exit = 1; }
            app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }, TaskScheduler.Default);
        Dispatcher.Run();
        return exit;
    }

    private static async Task RunAsync(string output)
    {
        var checks = new CheckSuite(output);
        try
        {
            checks.CoreTests();
            checks.CompactIndexTests();
            await checks.CacheTestsAsync();
            await checks.IndexAndUiTestsAsync();
            await checks.StartupUiTestsAsync();
            checks.Benchmark();
            checks.VerifyNoVisibleWindows();
        }
        finally
        {
            string report = JsonSerializer.Serialize(checks.Report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "verification.json"), report);
            Console.WriteLine(report);
        }
    }
}
