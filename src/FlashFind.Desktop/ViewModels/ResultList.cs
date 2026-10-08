// 文件用途：全部结果的延迟视图；只为可见行创建展示对象。
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using FlashFind.Core;

namespace FlashFind.Desktop.ViewModels;

public sealed class FileResult(FileEntry entry, ResultList? owner = null, int sourceIndex = -1, int generation = 0)
{
    internal WeakReference<ResultList>? Owner { get; } = owner is null ? null : new(owner);
    internal int SourceIndex { get; } = sourceIndex;
    internal int Generation { get; } = generation;
    public FileEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Path => Entry.ParentPath;
    public string FullPath => Entry.FullPath;
    public string Drive => Entry.Drive.TrimEnd('\\');
    public string Extension => Entry.IsDirectory ? "目录" : Entry.Extension.TrimStart('.').ToUpperInvariant() is { Length: > 0 } value ? value : "文件";
    public string Category => Entry.Category switch
    {
        FileCategory.Image => "图片", FileCategory.Video => "视频", _ => "文件"
    };
    public string Modified => Entry.ModifiedUtcTicks > 0 ? new DateTime(Entry.ModifiedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/MM/dd") : "—";
    public string Size => Entry.IsDirectory || Entry.Length < 0 ? "—" : FormatBytes(Entry.Length);
    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes; int order = 0;
        while (size >= 1024 && order < suffixes.Length - 1) { size /= 1024; order++; }
        return $"{size:0.#} {suffixes[order]}";
    }
}

public sealed class ResultList : IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private const int MaximumCachedRows = 4096;
    private readonly Dictionary<int, FileResult> _cache = new();
    private IReadOnlyList<FileEntry> _entries;
    private int _generation;
    internal int MaterializedRows { get; private set; }
    public ResultList(IReadOnlyList<FileEntry> entries) => _entries = entries;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Count => _entries.Count;
    public void Update(IReadOnlyList<FileEntry> entries)
    {
        _entries = entries; _generation++; _cache.Clear(); MaterializedRows = 0;
        PropertyChanged?.Invoke(this, new(nameof(Count))); PropertyChanged?.Invoke(this, new("Item[]"));
        CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
    }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public object? this[int index]
    {
        get
        {
            if (_cache.TryGetValue(index, out FileResult? result)) return result;
            if (_cache.Count >= MaximumCachedRows) _cache.Clear();
            MaterializedRows++;
            return _cache[index] = new(_entries[index], this, index, _generation);
        }
        set => throw new NotSupportedException();
    }
    public IEnumerator GetEnumerator() { for (int index = 0; index < Count; index++) yield return this[index]; }
    public int IndexOf(object? value) => value is FileResult result && result.Owner is not null &&
        result.Owner.TryGetTarget(out ResultList? owner) && ReferenceEquals(owner, this) &&
        result.Generation == _generation &&
        (uint)result.SourceIndex < (uint)Count ? result.SourceIndex : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public void CopyTo(Array array, int index)
    { for (int item = 0; item < Count; item++) array.SetValue(this[item], index + item); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
