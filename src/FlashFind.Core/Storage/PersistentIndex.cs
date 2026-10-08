// 文件用途：打开预制索引并按序号读取元数据；目录文本仅为实际显示的结果缓存。
using System.Collections.Concurrent;
using System.Text;
using FlashFind.Core.Search;

namespace FlashFind.Core.Storage;

internal readonly record struct PersistentRecord(int ParentId, int Flags, long NameOffset, int NameLength,
    long Length, long ModifiedTicks)
{
    public bool IsDirectory => (Flags & 1) != 0;
    public bool Unicode => (Flags & 2) != 0;
    public FileCategory Category => (FileCategory)(Flags >> 8);
}

internal sealed class PersistentIndex
{
    private const int MaximumCachedParents = 4096;
    private readonly MappedIndexFile _file;
    private readonly PersistentNamePage[] _asciiPages, _unicodePages;
    private readonly ConcurrentDictionary<int, string> _parentCache = new();
    public ReadyIndexHeader Header { get; }
    public string[] Roots { get; }
    public string[] Excluded { get; }
    public DateTime SavedUtc => new(Header.SavedTicks, DateTimeKind.Utc);

    private PersistentIndex(FileStream stream, ReadyIndexHeader header, string[] roots, string[] excluded)
    {
        Header = header; Roots = roots; Excluded = excluded;
        _file = new MappedIndexFile(stream);
        var ascii = new List<PersistentNamePage>(); var unicode = new List<PersistentNamePage>();
        long offset = header.PagesOffset;
        for (int index = 0; index < header.PageCount; index++)
        {
            var page = new PersistentNamePage(_file, offset);
            (page.Unicode ? unicode : ascii).Add(page); offset = page.EndOffset;
        }
        if (offset != header.FileLength) throw new InvalidDataException("直接查询索引的尾部异常。");
        _asciiPages = ascii.ToArray(); _unicodePages = unicode.ToArray();
    }

    public static PersistentIndex Open(string path, SourceStamp expected)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            (ReadyIndexHeader header, string[] roots, string[] excluded) = ReadMetadata(stream, expected);
            return new PersistentIndex(stream, header, roots, excluded);
        }
        catch { stream.Dispose(); throw; }
    }

    public static ReadyIndexInfo ReadInfo(string path, SourceStamp expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        (ReadyIndexHeader header, string[] roots, string[] excluded) = ReadMetadata(stream, expected);
        return new(roots, excluded, new(header.SavedTicks, DateTimeKind.Utc), header.EntryCount, header.FileCount, header.FileLength);
    }

    private static (ReadyIndexHeader Header, string[] Roots, string[] Excluded) ReadMetadata(FileStream stream, SourceStamp expected)
    {
        ReadyIndexHeader header = ReadyIndexFormat.ReadHeader(stream);
        if (header.Source != expected) throw new InvalidDataException("直接查询索引需要与现有缓存重新同步。");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        string[] roots = ReadPaths(reader), excluded = ReadPaths(reader);
        if (stream.Position > header.RecordsOffset) throw new InvalidDataException("索引元数据越界。");
        return (header, roots, excluded);
    }

    private static string[] ReadPaths(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > 1000) throw new InvalidDataException("索引范围数量异常。");
        var paths = new string[count];
        for (int index = 0; index < count; index++)
        {
            paths[index] = reader.ReadString();
            if (paths[index].Length > ReadyIndexFormat.MaximumPathLength || !Path.IsPathFullyQualified(paths[index]))
                throw new InvalidDataException("索引范围路径异常。");
        }
        return paths;
    }

    public PersistentRecord ReadRecord(int index)
    {
        if ((uint)index >= (uint)Header.EntryCount) throw new InvalidDataException("索引条目序号异常。");
        long offset = Header.RecordsOffset + (long)index * ReadyIndexFormat.EntrySize;
        var record = new PersistentRecord(_file.ReadInt32(offset), _file.ReadInt32(offset + 4), _file.ReadInt64(offset + 8),
            _file.ReadInt32(offset + 16), _file.ReadInt64(offset + 24), _file.ReadInt64(offset + 32));
        if ((uint)record.ParentId >= (uint)Header.ParentCount || record.NameLength is < 0 or > ReadyIndexFormat.MaximumPathLength ||
            record.NameOffset < Header.StringsOffset || record.NameOffset > Header.PagesOffset - (long)record.NameLength * sizeof(char) ||
            (record.Flags & 252) != 0 || record.Category is < FileCategory.All or > FileCategory.Other ||
            record.ModifiedTicks < 0 || record.ModifiedTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("索引条目的字段异常。");
        return record;
    }

    public ReadOnlySpan<char> ReadName(PersistentRecord record) => _file.Chars(record.NameOffset, record.NameLength);

    public ReadOnlySpan<char> ReadParent(int index)
    {
        if ((uint)index >= (uint)Header.ParentCount) throw new InvalidDataException("索引目录序号异常。");
        long location = Header.ParentsOffset + (long)index * ReadyIndexFormat.ParentSize;
        long offset = _file.ReadInt64(location); int length = _file.ReadInt32(location + 8);
        if (length is < 0 or > ReadyIndexFormat.MaximumPathLength || offset < Header.StringsOffset ||
            offset > Header.PagesOffset - (long)length * sizeof(char)) throw new InvalidDataException("索引目录字段异常。");
        return _file.Chars(offset, length);
    }

    public FileEntry GetEntry(int index)
    {
        try
        {
            PersistentRecord record = ReadRecord(index);
            if (!_parentCache.TryGetValue(record.ParentId, out string? parent))
            {
                parent = ReadParent(record.ParentId).ToString();
                if (_parentCache.Count < MaximumCachedParents) parent = _parentCache.GetOrAdd(record.ParentId, parent);
            }
            return new(parent, ReadName(record).ToString(), record.IsDirectory, record.Length, record.ModifiedTicks);
        }
        finally { GC.KeepAlive(_file); }
    }

    public int FileIndex(int index)
    {
        if ((uint)index >= (uint)Header.FileCount) throw new ArgumentOutOfRangeException(nameof(index));
        int result = _file.ReadInt32(Header.FilesOffset + (long)index * sizeof(int));
        GC.KeepAlive(_file);
        if ((uint)result >= (uint)Header.EntryCount) throw new InvalidDataException("文件序号异常。");
        return result;
    }

    public IEnumerable<int> Candidates(SearchQuery query, CancellationToken cancellation)
    {
        bool unicode = query.Terms.Any(term => !term.Excluded && term.RequiresUnicode) && !query.IncludePath;
        QueryTerm? seed = query.IncludePath ? null : query.Terms.Where(term => !term.Excluded && !term.Wildcard)
            .MaxBy(term => term.Normalized.Length);
        if (seed is null) return Enumerable.Range(0, Header.EntryCount);
        byte[]? asciiSeed = seed.RequiresUnicode ? null : Encoding.ASCII.GetBytes(seed.Normalized);
        return unicode ? Lookup(_unicodePages, seed.Normalized, asciiSeed, cancellation)
            : SearchCatalog.Merge(Lookup(_asciiPages, seed.Normalized, asciiSeed, cancellation),
                Lookup(_unicodePages, seed.Normalized, asciiSeed, cancellation));
    }

    private static IEnumerable<int> Lookup(PersistentNamePage[] pages, string seed, byte[]? asciiSeed, CancellationToken cancellation)
    {
        foreach (PersistentNamePage page in pages)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (int index in page.Find(seed, asciiSeed)) yield return index;
        }
    }
}
