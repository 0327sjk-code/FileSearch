// 文件用途：全部文件的序号视图和排序分桶视图；避免复制百万条结果引用。
using System.Collections;

namespace FlashFind.Core.Search;

internal sealed class IndexedEntries(FileEntry[] entries, int[] indices) : IReadOnlyList<FileEntry>
{
    public int Count => indices.Length;
    public FileEntry this[int index] => entries[indices[index]];
    public IEnumerator<FileEntry> GetEnumerator()
    { foreach (int index in indices) yield return entries[index]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class RankedEntries : IReadOnlyList<FileEntry>
{
    private readonly List<FileEntry>[] _buckets;
    private readonly int[] _ends;
    public int Count { get; }
    public RankedEntries(List<FileEntry>?[] buckets)
    {
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
            return _buckets[bucket][index - (bucket == 0 ? 0 : _ends[bucket - 1])];
        }
    }
    public IEnumerator<FileEntry> GetEnumerator()
    { foreach (List<FileEntry> bucket in _buckets) foreach (FileEntry entry in bucket) yield return entry; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
