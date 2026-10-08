// 文件用途：实际启动流程和范围切换后的下拉文字验证；测试硬盘仅为隔离目录。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FlashFind.Core.Indexing;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Services;
using FlashFind.Desktop.ViewModels;
using FlashFind.Desktop.Views;

namespace FlashFind.Verification;

internal sealed partial class CheckSuite
{
    public async Task StartupUiTestsAsync()
    {
        string root = Path.Combine(output, "startup-fixtures");
        string first = Path.Combine(root, "A"), second = Path.Combine(root, "B");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "启动样本_A.txt"), "fixture");
        File.WriteAllText(Path.Combine(second, "启动样本_B.txt"), "fixture");
        IReadOnlyList<IndexedDrive> drives = new[]
        {
            new IndexedDrive(first, "测试 A", "NTFS", 17, true, false),
            new IndexedDrive(second, "测试 B", "NTFS", 17, true, false)
        };
        var log = new AppLog(Path.Combine(root, "logs"));
        var index = new IndexService(new CacheStore(Path.Combine(root, "cache.bin.gz")), log, preferNtfs: false);
        var model = new MainViewModel(index, new FileActions(new RecordingShell()), log,
            new AppSettings(), Path.Combine(root, "settings.json"), Dispatcher.CurrentDispatcher, () => drives);
        var window = new MainWindow(model);
        try
        {
            Render(window, Path.Combine(output, "startup-before.png"), 1140, 748);
            var dropdown = (ComboBox)window.FindName("DriveDropdown");
            object defaultChoice = model.Drive;
            await model.StartAsync();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Render(window, Path.Combine(output, "startup-after.png"), 1140, 748);
            Check("首次启动后默认磁盘选项保持选中", ReferenceEquals(dropdown.SelectedItem, defaultChoice));
            Check("首次启动后下拉实际显示全部已选硬盘", DropdownText(dropdown) == "全部已选硬盘");
            dropdown.SelectedIndex = 1;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Render(window, Path.Combine(output, "startup-single-drive.png"), 1140, 748);
            Check("磁盘下拉实际显示单盘名称", DropdownText(dropdown) == model.Drive.Title && model.Drive.Root == first);
            model.SettingsCommand.Execute(null);
            model.DriveChoices[0].Selected = false;
            model.SaveSettingsCommand.Execute(null);
            await UntilAsync(() => !model.SavingSettings && index.Snapshot.Any(entry => entry.Name == "启动样本_B.txt") &&
                index.Snapshot.All(entry => !entry.Name.Contains("_A")));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Render(window, Path.Combine(output, "startup-after-scope-change.png"), 1140, 748);
            Check("切换范围后下拉默认文字不为空", DropdownText(dropdown) == "全部已选硬盘" && model.Drive.Root is null);
            Check("默认项与绑定模型保持同一对象", ReferenceEquals(dropdown.SelectedItem, model.Drive));
        }
        finally { await model.DisposeAsync(); window.Close(); }
    }

    private static string DropdownText(ComboBox dropdown)
    {
        var presenter = (ContentPresenter)dropdown.Template.FindName("SelectionLabel", dropdown);
        presenter.ApplyTemplate(); presenter.UpdateLayout();
        return string.Concat(Descendants<TextBlock>(presenter).Select(text => text.Text));
    }
}
