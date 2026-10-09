// 文件用途：检索交互、后台任务、硬盘设置及操作反馈。
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Threading;
using FlashFind.Core;
using FlashFind.Core.Indexing;
using FlashFind.Core.Search;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Services;

namespace FlashFind.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int SearchDebounceMilliseconds = 75;
    private readonly IndexService _index;
    private readonly IFileActions _actions;
    private readonly AppLog _log;
    private readonly string _settingsPath;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly SearchEngine _engine = new();
    private readonly Func<IReadOnlyList<IndexedDrive>> _discoverDrives;
    private CancellationTokenSource? _searchCancellation;
    private Task? _startup;
    private AppSettings _settings;
    private string _query = "", _status = "准备建立文件索引", _summary = "输入文件名，立即查找", _timing = "", _toast = "", _scope = "全部内置硬盘";
    private bool _scanning, _searching, _settingsOpen, _savingSettings, _disposed;
    private int _indexedCount;
    private SearchCatalog? _observedCatalog;
    private readonly ResultList _results = new(Array.Empty<FileEntry>());
    private FileResult? _selected;
    private CategoryChoice _category;
    private DriveFilter _drive;

    public MainViewModel(IndexService index, IFileActions actions, AppLog log,
        AppSettings settings, string settingsPath, Dispatcher dispatcher,
        Func<IReadOnlyList<IndexedDrive>>? discoverDrives = null)
    {
        _index = index; _actions = actions; _log = log; _settings = settings;
        _settingsPath = settingsPath; _dispatcher = dispatcher;
        _discoverDrives = discoverDrives ?? DriveDiscovery.InternalDrives;
        _category = Categories[0]; _drive = new("全部已选硬盘", null); DriveFilters.Add(_drive);
        _searchTimer = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = SearchNowAsync(); };
        _toastTimer = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = ""; };
        OpenCommand = new RelayCommand(value => Act(value, _actions.Open, "已打开文件"), HasFile);
        LocateCommand = new RelayCommand(value => Act(value, _actions.Locate, "已在文件夹中定位"), HasFile);
        CopyCommand = new RelayCommand(value => Act(value, _actions.CopyPath, "路径已复制"), HasFile);
        ClearCommand = new RelayCommand(_ => Query = "");
        SettingsCommand = new RelayCommand(_ => OpenSettings());
        CloseSettingsCommand = new RelayCommand(_ => SettingsOpen = false, _ => !SavingSettings);
        SaveSettingsCommand = new RelayCommand(_ => _ = SaveSettingsAsync(), _ => !SavingSettings);
        SelectAllDrivesCommand = new RelayCommand(_ => { foreach (DriveChoice item in DriveChoices) item.Selected = true; });
        RescanCommand = new RelayCommand(_ => _ = _index.RebuildAsync(), _ => !Scanning);
        _index.Changed += IndexChanged;
        IndexChanged(_index.State);
    }

    public IReadOnlyList<CategoryChoice> Categories { get; } = new[]
    {
        new CategoryChoice("文件", FileCategory.All), new("图片", FileCategory.Image), new("视频", FileCategory.Video)
    };
    public ObservableCollection<DriveFilter> DriveFilters { get; } = new();
    public ObservableCollection<DriveChoice> DriveChoices { get; } = new();
    public ICommand OpenCommand { get; }
    public ICommand LocateCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand SelectAllDrivesCommand { get; }
    public ICommand RescanCommand { get; }
    public AppSettings Settings => _settings;
    public UpdateViewModel? Updates { get; private set; }
    public void AttachUpdates(UpdateViewModel updates) { Updates = updates; Raise(nameof(Updates)); }
    internal void RecordDockState(string state) => _log.Write("Floating search: " + state);
    public bool SaveDockSettings(DockSettings dock)
    {
        try
        {
            AppSettings updated = _settings with { Dock = dock.Normalize() };
            updated.Save(_settingsPath); _settings = updated; Raise(nameof(Settings));
            return true;
        }
        catch (Exception error)
        { _log.Write("Dock settings save failed", error); ShowToast("悬浮栏位置保存失败：" + error.Message); return false; }
    }
    public string Query { get => _query; set { if (Set(ref _query, value)) ScheduleSearch(); } }
    public CategoryChoice Category { get => _category; set { if (value is not null && Set(ref _category, value)) ScheduleSearch(); } }
    public DriveFilter Drive
    {
        get => _drive;
        set
        {
            if (value is null || ReferenceEquals(_drive, value)) return;
            _drive = value; Raise(); ScheduleSearch();
        }
    }
    public ResultList Results => _results;
    public FileResult? Selected { get => _selected; set { if (Set(ref _selected, value)) Raise(nameof(SelectionText)); } }
    public string SelectionText => Selected?.FullPath ?? "双击打开文件 · Enter 打开 · Alt+Enter 定位 · Ctrl+C 复制路径";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Timing { get => _timing; private set => Set(ref _timing, value); }
    public string Scope { get => _scope; private set => Set(ref _scope, value); }
    public string Toast { get => _toast; private set => Set(ref _toast, value); }
    public string IndexCount => $"{_indexedCount:N0} 项已索引";
    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool Searching { get => _searching; private set => Set(ref _searching, value); }
    public bool SettingsOpen { get => _settingsOpen; private set => Set(ref _settingsOpen, value); }
    public bool SavingSettings { get => _savingSettings; private set { if (Set(ref _savingSettings, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool Empty => Results.Count == 0;
    public string EmptyTitle => _indexedCount == 0 && Scanning ? "正在建立索引" : string.IsNullOrWhiteSpace(Query) ? "开始查找你的文件" : "没有匹配的文件";
    public string EmptyDetail => _indexedCount == 0 && Scanning ? "首次扫描完成后，搜索会更快。你可以现在输入文件名。" : string.IsNullOrWhiteSpace(Query) ? "输入文件名或其中几个字，结果会显示在这里。" : "试试更短的文件名，或在硬盘设置中检查检索范围。";

    public Task StartAsync(CancellationToken cancellation = default)
    {
        IReadOnlyList<IndexedDrive> available = _discoverDrives();
        string[] roots = _settings.UseAllInternalDrives ? available.Select(x => x.Root).ToArray()
            : available.Select(x => x.Root).Where(root => _settings.Roots.Contains(root, StringComparer.OrdinalIgnoreCase)).ToArray();
        UpdateDriveFilters(roots);
        _startup = _index.StartAsync(roots, cancellation);
        return _startup;
    }

    private void IndexChanged(IndexState state)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _indexedCount = state.Count; Raise(nameof(IndexCount));
            Scanning = state.IsScanning; Status = state.Message;
            RaiseEmpty();
            SearchCatalog catalog = _index.Catalog;
            if (!ReferenceEquals(_observedCatalog, catalog)) { _observedCatalog = catalog; ScheduleSearch(); }
        }, DispatcherPriority.Background);
    }

    private void ScheduleSearch()
    {
        if (_disposed) return;
        _searchCancellation?.Cancel(); _searchTimer.Stop(); _searchTimer.Start();
    }

    public async Task SearchNowAsync()
    {
        if (_disposed) return;
        _searchTimer.Stop(); _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        string text = Query; FileCategory category = Category.Category; string? root = Drive.Root;
        Searching = true;
        try
        {
            SearchQuery query = SearchQuery.Parse(text, category, root, includePath: false);
            SearchCatalog catalog = _index.Catalog;
            SearchResponse response = await Task.Run(() => _engine.Search(catalog, query, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || _disposed) return;
            string? selectedPath = Selected?.FullPath;
            // 清掉表格的旧选择和当前项，避免 Reset 时逐个寻找旧行对象。
            Selected = null;
            CollectionViewSource.GetDefaultView(Results).MoveCurrentToPosition(-1);
            Results.Update(response.Items); Raise(nameof(Results));
            // 不全量创建行对象；选中位置只在有旧选择时检索。
            int selectedIndex = selectedPath is null ? -1 : FindPath(response.Items, selectedPath);
            Selected = Results.Count == 0 ? null : (FileResult?)Results[selectedIndex < 0 ? 0 : selectedIndex];
            Summary = $"{response.TotalMatches:N0} 个{(category == FileCategory.Folder ? "文件夹" : "文件")}{(text.Length > 0 ? "符合文件名" : "")}";
            Timing = $"{response.ElapsedMilliseconds:0.#} ms";
            RaiseEmpty();
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException error) { ShowToast(error.Message); }
        catch (Exception error) { _log.Write("Search failed", error); ShowToast("搜索失败，请重新输入文件名。"); }
        finally { if (ReferenceEquals(_searchCancellation, cancellation)) Searching = false; }
    }

    private static int FindPath(IReadOnlyList<FileEntry> entries, string path)
    {
        FileIdentity identity = FileIdentity.FromPath(path);
        if (entries is IFileIdentityLookup lookup) return lookup.FindIdentity(identity);
        for (int index = 0; index < entries.Count; index++)
            if (FileIdentity.Comparer.Equals(entries[index].Identity, identity)) return index;
        return -1;
    }

    private bool HasFile(object? parameter) => parameter is FileResult || Selected is not null;
    private void Act(object? parameter, Action<FileEntry> action, string success)
    {
        FileResult? file = parameter as FileResult ?? Selected;
        if (file is null) return;
        try { action(file.Entry); ShowToast(success); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { _log.Write("File action failed", error); ShowToast(error is FileNotFoundException ? error.Message : "操作失败：" + error.Message); }
    }
    private void ShowToast(string message)
    { Toast = message; _toastTimer.Stop(); _toastTimer.Start(); }
    private void RaiseEmpty() { Raise(nameof(Empty)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDetail)); }

    private void OpenSettings()
    {
        DriveChoices.Clear();
        foreach (IndexedDrive drive in _discoverDrives())
            DriveChoices.Add(new(drive, _settings.UseAllInternalDrives || _index.Roots.Contains(drive.Root, StringComparer.OrdinalIgnoreCase)));
        SettingsOpen = true;
    }

    private async Task SaveSettingsAsync()
    {
        string[] roots = DriveChoices.Where(x => x.Selected).Select(x => x.Root).ToArray();
        if (roots.Length == 0) { ShowToast("请至少选择一个内置硬盘。"); return; }
        SavingSettings = true;
        try
        {
            AppSettings updated = _settings with { Roots = roots, UseAllInternalDrives = false, IncludePath = false };
            updated.Save(_settingsPath); _settings = updated;
            UpdateDriveFilters(roots); SettingsOpen = false;
            ShowToast("硬盘设置已保存，正在更新索引");
            SavingSettings = false;
            await Task.Run(() => _index.ChangeRootsAsync(roots));
        }
        catch (Exception error) { _log.Write("Settings update failed", error); ShowToast("硬盘设置保存失败：" + error.Message); }
        finally { SavingSettings = false; }
    }

    private void UpdateDriveFilters(string[] roots)
    {
        // 默认选项始终留在列表中，避免重建时 SelectedItem 被清空。
        while (DriveFilters.Count > 1) DriveFilters.RemoveAt(DriveFilters.Count - 1);
        foreach (string root in roots) DriveFilters.Add(new(root.TrimEnd('\\'), root));
        Drive = DriveFilters[0]; Raise(nameof(Drive));
        Scope = roots.Length == 0 ? "没有可用的内置硬盘" : string.Join(" · ", roots.Select(x => x.TrimEnd('\\')));
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true; _searchTimer.Stop(); _toastTimer.Stop(); _searchCancellation?.Cancel();
        _index.Changed -= IndexChanged;
        await _index.DisposeAsync();
        if (_startup is not null)
            try { await _startup; } catch (OperationCanceledException) { }
        _searchCancellation?.Dispose();
    }
}
