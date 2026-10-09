// 文件用途：启动、单实例、异常日志和退出前保存；开发验证可在独立后台入口运行。
using System.IO;
using System.Windows;
using FlashFind.Core.Indexing;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Services;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;
using FlashFind.Desktop.Updating;

namespace FlashFind.Desktop;

public partial class App : Application
{
    private Mutex? _instance;
    private MainViewModel? _viewModel;
    private AppLog? _log;
    private DesktopSession? _session;
    private UpdateController? _updates;
    private bool _closing;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        if (TryStartUpdateCommand(args.Args)) return;
        if (args.Args.Length == 3 && args.Args[0] is "--verify-startup" or "--verify-startup-background" or "--verify-startup-floating")
        { _ = VerifyStartupAsync(args.Args[1], args.Args[2], args.Args[0] == "--verify-startup-background", args.Args[0] == "--verify-startup-floating"); return; }
        if (args.Args.Length == 2 && args.Args[0] == "--verify")
        { _ = VerifyPackageAsync(args.Args[1]); return; }
        _instance = new Mutex(true, "Local\\FlashFind.Desktop", out bool created);
        if (!created) { NativeTrayIcon.RestoreExisting(); Shutdown(); return; }
        string data = AppSettings.DataDirectory;
        _log = new AppLog(data);
        UpdateReceipt? receipt = UpdateHandoff.ReadReceipt(args.Args, _log);
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
            eventArgs.Cancel = true; _closing = true; _updates?.Dispose(); _session?.Dispose();
            try
            {
                (_viewModel.Settings with { WindowWidth = window.RestoreBounds.IsEmpty ? window.Width : window.RestoreBounds.Width,
                    WindowHeight = window.RestoreBounds.IsEmpty ? window.Height : window.RestoreBounds.Height }).Save(settingsPath);
                await _viewModel.DisposeAsync();
            }
            catch (Exception error) { _log.Write("Shutdown failed", error); }
            finally { window.Close(); Shutdown(); }
        };
        _ = StartIndexAsync();
        try { _session = new DesktopSession(window, _viewModel); }
        catch (Exception error) { _log.Write("Notification area initialization failed", error); }
        if (receipt is not null) UpdateResume.Apply(_viewModel, receipt.Plan.Resume);
        if (receipt?.Plan.Resume.InTray == true && _session is not null) _session.MoveToTray();
        else window.Show();
        _updates = new(_viewModel, _log, Dispatcher,
            () => UpdateResume.Capture(_viewModel, _session?.IsInTray == true), () => window.Close(),
            message => _session?.NotifyUpdate(message));
        if (_session is not null) _session.CheckUpdatesRequested += _updates.CheckManually;
        if (receipt is not null) UpdateHandoff.Acknowledge(receipt, _log);
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
    private async Task VerifyStartupAsync(string cachePath, string output, bool completeBackgroundLoad, bool verifyFloating)
    {
        try { await StartupDiagnostics.RunAsync(cachePath, output, completeBackgroundLoad, verifyFloating); Shutdown(0); }
        catch (Exception error)
        { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "startup-failure.txt"), error.ToString()); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs args)
    {
        _instance?.Dispose();
        _updates?.Dispose();
        _session?.Dispose();
        base.OnExit(args);
    }
}
