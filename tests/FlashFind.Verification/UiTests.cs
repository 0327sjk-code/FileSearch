// 文件用途：真实磁盘变化回归、真实 WPF 控件离屏渲染、定位命令后台验证。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashFind.Core;
using FlashFind.Core.Indexing;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Services;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Verification;

internal sealed partial class CheckSuite
{
    public async Task IndexAndUiTestsAsync()
    {
        string fixture = Path.Combine(output, "fixtures", "A");
        string other = Path.Combine(output, "fixtures", "B");
        Directory.CreateDirectory(fixture); Directory.CreateDirectory(other);
        string[] friendly = { "水面材质_柔和波纹.uasset", "水面材质_透明折射.uasset", "水面材质_泡沫边缘.uasset", "水面材质_海岸浪花.uasset", "水面材质_浅滩颜色.uasset", "水面材质_湖泊镜面.uasset", "水面材质_河流流速.uasset", "水面材质_雨滴涟漪.uasset", "水面材质_夜景反射.uasset", "水面材质_远景过渡.uasset" };
        foreach (string name in friendly) File.WriteAllText(Path.Combine(fixture, name), "filename search must not read this content");
        for (int index = 0; index < 2500; index++) File.WriteAllText(Path.Combine(fixture, $"asset_{index:0000}.txt"), "水面内容不会被检索");
        string special = Path.Combine(fixture, "中文 空格 [1], &(a).txt");
        File.WriteAllText(special, "fixture");
        File.WriteAllText(Path.Combine(other, "另一个硬盘范围.txt"), "fixture");
        var log = new AppLog(Path.Combine(output, "logs"));
        var indexService = new IndexService(new CacheStore(Path.Combine(output, "live.bin.gz")), log,
            new[] { Path.Combine(output, "logs") }, preferNtfs: true);
        await indexService.StartAsync(new[] { fixture });
        Check("真实目录首次索引", indexService.Snapshot.Count(x => !x.IsDirectory) == 2511);
        string created = Path.Combine(fixture, "新建的文件.txt");
        File.WriteAllText(created, "fixture");
        await UntilAsync(() => indexService.Snapshot.Any(x => x.FullPath == created));
        Check("新增文件自动入索引", true);
        string renamed = Path.Combine(fixture, "重命名后的文件.txt");
        File.Move(created, renamed);
        await UntilAsync(() => indexService.Snapshot.Any(x => x.FullPath == renamed) && indexService.Snapshot.All(x => x.FullPath != created));
        Check("文件重命名移除旧路径", true);
        File.Delete(renamed);
        await UntilAsync(() => indexService.Snapshot.All(x => x.FullPath != renamed));
        Check("删除文件自动移除", true);
        string directory = Path.Combine(fixture, "移动目录");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "子文件.txt"), "fixture");
        await UntilAsync(() => indexService.Snapshot.Any(x => x.Name == "子文件.txt"));
        string destination = Path.Combine(fixture, "新目录名");
        Directory.Move(directory, destination);
        await UntilAsync(() => indexService.Snapshot.Any(x => x.FullPath == Path.Combine(destination, "子文件.txt")) && indexService.Snapshot.All(x => !PathRules.IsWithin(x.FullPath, directory)));
        Check("目录重命名更新所有子路径", true);

        var gateway = new RecordingShell();
        var actions = new FileActions(gateway);
        actions.Locate(FileEntry.FromPath(special, false));
        Check("真实 Windows PIDL 解析中文特殊路径", gateway.SelectCalls == 1 && gateway.LastItem != IntPtr.Zero);
        actions.Open(FileEntry.FromPath(special, false)); actions.CopyPath(FileEntry.FromPath(special, false));
        Check("打开和复制保持完整路径", gateway.Opened == special && gateway.Copied == special);
        Check("过期索引不会打开错误位置", Throws<FileNotFoundException>(() => actions.Locate(FileEntry.FromPath(Path.Combine(fixture, "不存在.txt"), false))));

        string settingsPath = Path.Combine(output, "ui-settings.json");
        var settings = new AppSettings { UseAllInternalDrives = false, Roots = new[] { fixture } };
        var vm = new MainViewModel(indexService, actions, log, settings, settingsPath, Dispatcher.CurrentDispatcher);
        var window = new MainWindow(vm);
        await vm.SearchNowAsync();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("界面不截断大量结果", vm.Results.Count == 2512);
        Render(window, Path.Combine(output, "all-files.png"), 1140, 748);
        int visibleRows = Descendants<DataGridRow>((DependencyObject)window.Content).Count();
        Check("表格只创建可见行", visibleRows is > 0 and < 40);
        vm.Query = "水面"; await vm.SearchNowAsync();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("界面查询只按文件名", vm.Results.Count == 10);
        Render(window, Path.Combine(output, "search.png"), 1140, 748);
        var dropdown = (ComboBox)window.FindName("DriveDropdown");
        dropdown.ApplyTemplate();
        Check("下拉框使用统一圆角模板", dropdown.Template.FindName("Chevron", dropdown) is System.Windows.Shapes.Path);
        vm.DriveFilters.Add(new("测试盘", fixture));
        dropdown.SelectedIndex = 1;
        Check("自定义下拉框保留磁盘选择功能", vm.Drive.Root == fixture);
        dropdown.SelectedIndex = 0;
        Button locate = Descendants<Button>((DependencyObject)window.Content).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "定位文件");
        int previous = gateway.SelectCalls;
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(locate).GetPattern(PatternInterface.Invoke);
        invoke.Invoke();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check("实际结果行定位按钮命令", gateway.SelectCalls == previous + 1);
        vm.SettingsCommand.Execute(null);
        await Task.Delay(240);
        Check("硬盘设置只显示内置设备", vm.DriveChoices.Count == DriveDiscovery.InternalDrives().Count);
        Render(window, Path.Combine(output, "settings.png"), 1140, 748);
        vm.CloseSettingsCommand.Execute(null);
        Render(window, Path.Combine(output, "compact.png"), 840, 510);
        vm.Query = "不存在的文件名"; await vm.SearchNowAsync();
        Check("空状态可见", vm.Empty && vm.Results.Count == 0);
        Render(window, Path.Combine(output, "empty.png"), 1140, 748);
        vm.SettingsCommand.Execute(null);
        vm.DriveChoices.Clear();
        vm.DriveChoices.Add(new(new(fixture, "测试 A", "NTFS", 17, true, false), false));
        vm.SaveSettingsCommand.Execute(null);
        Check("禁止空硬盘选择", vm.Toast.Contains("至少选择"));
        vm.DriveChoices.Add(new(new(other, "测试 B", "NTFS", 17, true, false), true));
        vm.SaveSettingsCommand.Execute(null);
        await UntilAsync(() => !vm.SavingSettings && indexService.Snapshot.Any(x => x.Name == "另一个硬盘范围.txt"));
        Check("硬盘设置立即切换索引范围", indexService.Snapshot.All(x => PathRules.IsWithin(x.FullPath, other)));
        AppSettings persisted = AppSettings.Load(settingsPath);
        Check("默认硬盘选择持久化", !persisted.UseAllInternalDrives && persisted.Roots.SequenceEqual(new[] { other }) && !persisted.IncludePath);
        await vm.DisposeAsync();
        window.Close();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Background condition did not become true.");
            await Task.Delay(100);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Render(MainWindow window, string file, int width, int height)
    {
        window.Width = width;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }

    private sealed class RecordingShell : IShellGateway
    {
        public int SelectCalls { get; private set; }
        public IntPtr LastItem { get; private set; }
        public string? Opened { get; private set; }
        public string? Copied { get; private set; }
        public int SelectItem(IntPtr item) { SelectCalls++; LastItem = item; return 0; }
        public void OpenPath(string path) => Opened = path;
        public void CopyText(string text) => Copied = text;
    }
}
