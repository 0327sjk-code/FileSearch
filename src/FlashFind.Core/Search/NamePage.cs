// 文件用途：连续名称页与 SIMD 子串查询；通过偏移表还原匹配条目。
using System.Text;

namespace FlashFind.Core.Search;

internal sealed class NamePage
{
    private readonly FileEntry[] _entries;
    private readonly int[] _offsets;
    private readonly string _text;

    public NamePage(FileEntry[] entries, int start, int count)
    {
        _entries = new FileEntry[count]; Array.Copy(entries, start, _entries, 0, count);
        _offsets = new int[count + 1];
        int length = 0;
        foreach (FileEntry entry in _entries) length = checked(length + entry.NormalizedName.Length + 1);
        var builder = new StringBuilder(length);
        for (int index = 0; index < count; index++)
        {
            _offsets[index] = builder.Length;
            builder.Append(_entries[index].NormalizedName); builder.Append('\0');
        }
        _offsets[count] = builder.Length; _text = builder.ToString();
    }

    public IEnumerable<FileEntry> Find(string normalized)
    {
        int position = 0;
        while (position < _text.Length)
        {
            int found = _text.IndexOf(normalized, position, StringComparison.Ordinal);
            if (found < 0) yield break;
            int entryIndex = Array.BinarySearch(_offsets, found);
            if (entryIndex < 0) entryIndex = ~entryIndex - 1;
            yield return _entries[entryIndex];
            // 同一名称只返回一次；跳过重复子串。
            position = _offsets[entryIndex + 1];
        }
    }
}
