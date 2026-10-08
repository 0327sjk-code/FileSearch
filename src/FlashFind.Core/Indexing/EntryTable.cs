// 文件用途：线程安全的紧凑身份表；批量发布快照并复用没有变化的条目。
namespace FlashFind.Core.Indexing;

internal sealed class EntryTable
{
    private readonly object _gate = new();
    private readonly Dictionary<FileIdentity, FileEntry> _entries = new(FileIdentity.Comparer);
    public int Count { get { lock (_gate) return _entries.Count; } }
    public void EnsureCapacity(int capacity) { lock (_gate) _entries.EnsureCapacity(capacity); }
    public bool Contains(string path) { lock (_gate) return _entries.ContainsKey(FileIdentity.FromPath(path)); }

    public FileEntry Upsert(FileEntry entry)
    {
        lock (_gate)
        {
            FileIdentity identity = entry.Identity;
            if (_entries.TryGetValue(identity, out FileEntry? previous) && previous == entry) return previous;
            _entries[identity] = entry;
            return entry;
        }
    }

    public FileEntry[] Snapshot() { lock (_gate) return _entries.Values.ToArray(); }
    public FileEntry? Remove(string path)
    {
        lock (_gate) return _entries.Remove(FileIdentity.FromPath(path), out FileEntry? entry) ? entry : null;
    }

    public void RemoveWhere(Func<FileEntry, bool> predicate)
    {
        lock (_gate)
        {
            var removed = new List<FileIdentity>();
            foreach (KeyValuePair<FileIdentity, FileEntry> pair in _entries)
                if (predicate(pair.Value)) removed.Add(pair.Key);
            foreach (FileIdentity identity in removed) _entries.Remove(identity);
        }
    }

    public void RenameTree(string oldPath, string newPath, EntryFactory factory)
    {
        lock (_gate)
        {
            FileEntry[] moving = _entries.Values.Where(entry => PathRules.IsWithin(entry, oldPath)).ToArray();
            foreach (FileEntry entry in moving)
            {
                _entries.Remove(entry.Identity);
                string destination = newPath + entry.FullPath[oldPath.Length..];
                FileEntry replacement = factory.FromPath(destination, entry.IsDirectory, entry.Length, entry.ModifiedUtcTicks);
                _entries[replacement.Identity] = replacement;
            }
        }
    }
}
