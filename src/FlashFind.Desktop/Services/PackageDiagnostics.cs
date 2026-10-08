// 文件用途：发布包后台自检，不显示窗口，不实际打开文件或资源管理器。
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashFind.Core;
using FlashFind.Core.Indexing;
using FlashFind.Core.Storage;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Desktop.Services;

internal static class PackageDiagnostics
{
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        string fixtures = Path.Combine(output, "fixtures"); Directory.CreateDirectory(fixtures);
        string file = Path.Combine(fixtures, "中文 空格 [1], &(文件).txt");
        File.WriteAllText(file, "诊断样本");
        string[] names = { "水面材质_浅滩.uasset", "水面材质_泡沫.uasset", "水面材质_折射.uasset", "项目截图.png", "项目预览.mp4" };
        foreach (string name in names) File.WriteAllText(Path.Combine(fixtures, name), "诊断样本");
        var log = new AppLog(Path.Combine(output, "logs"));
        var index = new IndexService(new CacheStore(Path.Combine(output, "index.bin.gz")), log, preferNtfs: false);
        await index.StartAsync(new[] { fixtures });
        var gateway = new DiagnosticGateway();
        var settings = new AppSettings { Roots = new[] { fixtures }, UseAllInternalDrives = true };
        var model = new MainViewModel(index, new FileActions(gateway), log, settings,
            Path.Combine(output, "settings.json"), Dispatcher.CurrentDispatcher);
        try
        {
            var window = new MainWindow(model);
            IReadOnlyDictionary<string, bool> placeholderChecks = await InputDiagnostics.VerifyAsync(window);
            if (window.Icon is not BitmapFrame icon || icon.Decoder?.Frames.Count != 10)
                throw new InvalidOperationException("程序窗口的多分辨率图标加载失败。");
            model.Query = "水面"; await model.SearchNowAsync();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (model.Results.Count != 3) throw new InvalidOperationException("发布包检索自检失败。");
            Capture(window, Path.Combine(output, "文件搜索.png"), 1140, 748);
            Capture(window, Path.Combine(output, "文件搜索_小窗口.png"), 840, 510);
            model.SettingsCommand.Execute(null);
            await Task.Delay(230);
            Capture(window, Path.Combine(output, "硬盘设置.png"), 1140, 748);
            model.LocateCommand.Execute(model.Results[0]);
            if (gateway.Locations != 1) throw new InvalidOperationException("发布包定位命令自检失败。");
            int visibleWindows = BackgroundWindowAudit.VisibleWindowCount();
            if (visibleWindows != 0) throw new InvalidOperationException("离屏验证意外创建了可见窗口。");
            object result = new { Passed = true, AppName = window.Title, FilesIndexed = index.Snapshot.Length,
                DefaultInternalDrives = DriveDiscovery.InternalDrives(), Categories = model.Categories.Select(x => x.Title),
                PlaceholderChecks = placeholderChecks, WindowIconFrames = icon.Decoder.Frames.Select(x => x.PixelWidth),
                LocateDispatches = gateway.Locations, ForegroundWindowsOpened = visibleWindows, ExplorerInvoked = false };
            File.WriteAllText(Path.Combine(output, "package-verification.json"), JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
        finally { await model.DisposeAsync(); }
    }

    private static void Capture(MainWindow window, string output, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        // 列宽在 SizeChanged 后重新测量；离屏模式也走同一布局逻辑。
        content.InvalidateMeasure(); content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(output); encoder.Save(file);
    }
    private sealed class DiagnosticGateway : IShellGateway
    {
        public int Locations { get; private set; }
        public int SelectItem(IntPtr absoluteItemIdList) { Locations++; return 0; }
        public void OpenPath(string path) { }
        public void CopyText(string text) { }
    }
}
