// 文件用途：在只读映射上查找名称子串；不复制完整名称页到托管堆。
namespace FlashFind.Core.Storage;

internal sealed class PersistentNamePage
{
    private readonly MappedIndexFile _file;
    private readonly bool _unicode;
    private readonly int _count, _length;
    private readonly long _indices, _offsets, _data;
    public bool Unicode => _unicode;
    public long EndOffset { get; }

    public PersistentNamePage(MappedIndexFile file, long offset)
    {
        _file = file;
        int kind = file.ReadInt32(offset);
        _count = file.ReadInt32(offset + 4); int bytes = file.ReadInt32(offset + 8);
        if (kind is < 0 or > 1 || _count is <= 0 or > ReadyIndexFormat.PageEntryCount ||
            bytes < _count * (kind + 1) || bytes > (long)_count * (ReadyIndexFormat.MaximumPathLength + 1) * (kind + 1) ||
            kind == 1 && (bytes & 1) != 0)
            throw new InvalidDataException("直接查询索引的名称页异常。");
        _unicode = kind == 1; _length = bytes / (kind + 1);
        _indices = offset + 12; _offsets = _indices + (long)_count * sizeof(int);
        _data = _offsets + (long)(_count + 1) * sizeof(int); EndOffset = _data + bytes;
        if (EndOffset > file.Length || file.ReadInt32(_offsets) != 0 ||
            file.ReadInt32(_offsets + (long)_count * sizeof(int)) != _length)
            throw new InvalidDataException("名称页的偏移范围异常。");
    }

    public IEnumerable<int> Find(string normalized, byte[]? asciiPattern)
    {
        if (!_unicode && asciiPattern is null) yield break;
        int position = 0;
        while (position < _length)
        {
            int found = FindOffset(normalized, asciiPattern, position);
            if (found < 0) yield break;
            int entry = _file.Integers(_offsets, _count + 1).BinarySearch(found);
            if (entry < 0) entry = ~entry - 1;
            if ((uint)entry >= (uint)_count) throw new InvalidDataException("名称页条目越界。");
            int next = _file.ReadInt32(_offsets + (long)(entry + 1) * sizeof(int));
            if (next <= found || next > _length) throw new InvalidDataException("名称页条目边界异常。");
            yield return _file.ReadInt32(_indices + (long)entry * sizeof(int));
            position = next;
        }
        GC.KeepAlive(_file);
    }

    private int FindOffset(string normalized, byte[]? asciiPattern, int position)
    {
        int relative = _unicode
            ? _file.Chars(_data, _length)[position..].IndexOf(normalized, StringComparison.Ordinal)
            : _file.Bytes(_data, _length)[position..].IndexOf(asciiPattern);
        GC.KeepAlive(_file);
        return relative < 0 ? -1 : position + relative;
    }
}
