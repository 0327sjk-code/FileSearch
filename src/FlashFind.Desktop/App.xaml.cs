// 文件用途：启动、单实例、异常日志和退出前保存；开发验证可在独立后台入口运行。
using System.IO;
using System.Windows;
using FlashFind.Core.Indexing;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Services;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop;

public partial class App : Application
{
    private Mutex? _instance;
    private MainViewModel? _viewModel;
    private AppLog? _log;
    private bool _closing;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        if (args.Args.Length == 2 && args.Args[0] == "--verify")
        { _ = VerifyPackageAsync(args.Args[1]); return; }
        _instance = new Mutex(true, "Local\\FlashFind.Desktop", out bool created);
        if (!created) { Shutdown(); return; }
        string data = AppSettings.DataDirectory;
        _log = new AppLog(data);
        DispatcherUnhandledException += (_, error) =>
        { _log.Write("Unhandled UI error", error.Exception); error.Handled = true; };
        if (!SystemParameters.ClientAreaAnimation) Resources["MotionDuration"] = new Duration(TimeSpan.Zero);
        string settingsPath = Path.Combine(data, "settings.json");
        AppSettings settings = AppSettings.Load(settingsPath);
        var index = new IndexService(new CacheStore(Path.Combine(data, "index.bin.gz")), _log, new[] { data }, settings.PreferNtfs);
        _viewModel = new MainViewModel(index, new FileActions(new NativeShellGateway()), _log, settings, settingsPath, Dispatcher);
        var window = new MainWindow(_viewModel)
        { Width = Math.Clamp(settings.WindowWidth, 840, SystemParameters.WorkArea.Width), Height = Math.Clamp(settings.WindowHeight, 540, SystemParameters.WorkArea.Height) };
        MainWindow = window;
        window.Closing += async (_, eventArgs) =>
        {
            if (_closing) return;
            eventArgs.Cancel = true; _closing = true;
            try
            {
                (_viewModel.Settings with { WindowWidth = window.RestoreBounds.Width, WindowHeight = window.RestoreBounds.Height }).Save(settingsPath);
                await _viewModel.DisposeAsync();
            }
            catch (Exception error) { _log.Write("Shutdown failed", error); }
            finally { window.Close(); Shutdown(); }
        };
        window.Show();
        _ = StartIndexAsync();
    }

    private async Task StartIndexAsync()
    {
        try { await _viewModel!.StartAsync(); }
        catch (Exception error) { _log?.Write("Startup indexing failed", error); }
    }
    private async Task VerifyPackageAsync(string output)
    {
        try { await PackageDiagnostics.RunAsync(output); Shutdown(0); }
        catch (Exception error)
        {
            Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "package-failure.txt"), error.ToString()); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs args)
    {
        _instance?.Dispose();
        base.OnExit(args);
    }
}
