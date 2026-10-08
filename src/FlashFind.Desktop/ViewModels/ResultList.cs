// 文件用途：全部结果的延迟视图；只为可见行创建展示对象。
using System.Collections;
using FlashFind.Core;

namespace FlashFind.Desktop.ViewModels;

public sealed class FileResult(FileEntry entry)
{
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

public sealed class ResultList(IReadOnlyList<FileEntry> entries) : IList
{
    private const int MaximumCachedRows = 4096;
    private readonly Dictionary<int, FileResult> _cache = new();
    public int Count => entries.Count;
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
            return _cache[index] = new(entries[index]);
        }
        set => throw new NotSupportedException();
    }
    public IEnumerator GetEnumerator() { for (int index = 0; index < Count; index++) yield return this[index]; }
    public int IndexOf(object? value) => value is FileResult result ? IndexOfEntry(result.Entry) : -1;
    private int IndexOfEntry(FileEntry entry)
    { for (int index = 0; index < Count; index++) if (ReferenceEquals(entries[index], entry)) return index; return -1; }
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public void CopyTo(Array array, int index)
    { for (int item = 0; item < Count; item++) array.SetValue(this[item], index + item); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
