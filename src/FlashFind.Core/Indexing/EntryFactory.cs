// 文件用途：索引生命周期内复用目录及常见文件名，避免永久全局字符串驻留。
namespace FlashFind.Core.Indexing;

public sealed class EntryFactory
{
    private const int MaximumPooledNames = 262_144;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    public FileEntry FromPath(string path, bool directory, long length = -1, long ticks = 0) =>
        Create(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), directory, length, ticks);

    public FileEntry Create(string parent, string name, bool directory, long length = -1, long ticks = 0)
    {
        parent = Path.TrimEndingDirectorySeparator(parent);
        lock (_gate)
        {
            if (_directories.TryGetValue(parent, out string? existingParent)) parent = existingParent;
            else _directories.Add(parent, parent);
            if (_names.TryGetValue(name, out string? existingName)) name = existingName;
            else if (_names.Count < MaximumPooledNames) _names.Add(name, name);
        }
        return new(parent, name, directory, length, ticks);
    }

    public void Trim(IEnumerable<FileEntry> entries)
    {
        lock (_gate)
        {
            _directories.Clear(); _names.Clear();
            foreach (FileEntry entry in entries)
            {
                _directories.TryAdd(entry.ParentPath, entry.ParentPath);
                if (_names.Count < MaximumPooledNames) _names.TryAdd(entry.Name, entry.Name);
            }
            _directories.TrimExcess(); _names.TrimExcess();
        }
    }
}
