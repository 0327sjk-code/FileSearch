// 文件用途：磁盘索引的延迟结果视图；结果列表只保存整数序号并支持无对象路径定位。
using System.Collections;
using FlashFind.Core.Storage;

namespace FlashFind.Core.Search;

public interface IFileIdentityLookup
{
    int FindIdentity(FileIdentity identity);
}

internal sealed class PersistentEntries(PersistentIndex source, int count, bool files, int[]? indices = null)
    : IReadOnlyList<FileEntry>, IFileIdentityLookup
{
    public int Count => indices?.Length ?? count;
    private int EntryIndex(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return indices is not null ? indices[index] : files ? source.FileIndex(index) : index;
    }
    public FileEntry this[int index] => source.GetEntry(EntryIndex(index));
    public IEnumerator<FileEntry> GetEnumerator() { for (int index = 0; index < Count; index++) yield return this[index]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int FindIdentity(FileIdentity identity)
    {
        try
        {
            for (int index = 0; index < Count; index++)
                if (PersistentIdentity.Equals(source, EntryIndex(index), identity)) return index;
            return -1;
        }
        finally { GC.KeepAlive(source); }
    }
}

internal sealed class PersistentRankedEntries : IReadOnlyList<FileEntry>, IFileIdentityLookup
{
    private readonly PersistentIndex _source;
    private readonly List<int>[] _buckets;
    private readonly int[] _ends;
    public int Count { get; }
    public PersistentRankedEntries(PersistentIndex source, List<int>?[] buckets)
    {
        _source = source;
        _buckets = buckets.Where(bucket => bucket is { Count: > 0 }).Select(bucket => bucket!).ToArray();
        _ends = new int[_buckets.Length];
        int count = 0;
        for (int index = 0; index < _buckets.Length; index++) _ends[index] = count += _buckets[index].Count;
        Count = count;
    }
    public FileEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int bucket = Array.BinarySearch(_ends, index + 1);
            if (bucket < 0) bucket = ~bucket;
            return _source.GetEntry(_buckets[bucket][index - (bucket == 0 ? 0 : _ends[bucket - 1])]);
        }
    }
    public IEnumerator<FileEntry> GetEnumerator()
    { foreach (List<int> bucket in _buckets) foreach (int index in bucket) yield return _source.GetEntry(index); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int FindIdentity(FileIdentity identity)
    {
        try
        {
            int index = 0;
            foreach (List<int> bucket in _buckets)
                foreach (int entry in bucket)
                { if (PersistentIdentity.Equals(_source, entry, identity)) return index; index++; }
            return -1;
        }
        finally { GC.KeepAlive(_source); }
    }
}

internal static class PersistentIdentity
{
    public static bool Equals(PersistentIndex source, int index, FileIdentity identity)
    {
        PersistentRecord record = source.ReadRecord(index);
        return source.ReadName(record).Equals(identity.Name, StringComparison.OrdinalIgnoreCase) &&
            source.ReadParent(record.ParentId).Equals(identity.ParentPath, StringComparison.OrdinalIgnoreCase);
    }
}
