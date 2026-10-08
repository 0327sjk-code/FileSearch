// 文件用途：紧凑名称页；ASCII 每字节一字，Unicode 保留 UTF-16，条目只按序号引用。
using System.Text;
using System.Runtime.InteropServices;

namespace FlashFind.Core.Search;

internal sealed class NamePage
{
    private readonly int[] _indices;
    private readonly int _start;
    private readonly int[] _offsets;
    private readonly byte[]? _ascii;
    private readonly string? _unicode;

    public NamePage(FileEntry[] entries, int[] indices, int start, int count, bool ascii)
    {
        _indices = indices; _start = start; _offsets = new int[count + 1];
        int length = 0;
        for (int index = 0; index < count; index++) length = checked(length + entries[indices[start + index]].Name.Length + 1);
        if (ascii)
        {
            _ascii = new byte[length];
            int position = 0;
            for (int index = 0; index < count; index++)
            {
                _offsets[index] = position;
                foreach (char character in entries[indices[start + index]].Name)
                    _ascii[position++] = (byte)(character is >= 'A' and <= 'Z' ? character + ('a' - 'A') : character);
                position++;
            }
            _offsets[count] = position;
        }
        else
        {
            var builder = new StringBuilder(length);
            for (int index = 0; index < count; index++)
            {
                _offsets[index] = builder.Length;
                builder.Append(entries[indices[start + index]].Name); builder.Append('\0');
            }
            _offsets[count] = builder.Length;
            _unicode = builder.ToString().ToLowerInvariant();
        }
    }

    public IEnumerable<int> Find(string normalized, byte[]? asciiPattern)
    {
        if (_ascii is not null && asciiPattern is null) yield break;
        int position = 0;
        while (position < _offsets[^1])
        {
            int found = FindOffset(normalized, asciiPattern, position);
            if (found < 0) yield break;
            int entryIndex = Array.BinarySearch(_offsets, found);
            if (entryIndex < 0) entryIndex = ~entryIndex - 1;
            yield return _indices[_start + entryIndex];
            // 同一名称只返回一次；跳过重复子串。
            position = _offsets[entryIndex + 1];
        }
    }

    private int FindOffset(string normalized, byte[]? asciiPattern, int position)
    {
        if (_ascii is null) return _unicode!.IndexOf(normalized, position, StringComparison.Ordinal);
        int relative = _ascii.AsSpan(position).IndexOf(asciiPattern);
        return relative < 0 ? -1 : position + relative;
    }

    internal void Write(BinaryWriter writer)
    {
        int count = _offsets.Length - 1;
        writer.Write(_ascii is null ? 1 : 0); writer.Write(count);
        writer.Write(_ascii?.Length ?? checked(_unicode!.Length * sizeof(char)));
        writer.Write(MemoryMarshal.AsBytes(_indices.AsSpan(_start, count)));
        writer.Write(MemoryMarshal.AsBytes(_offsets.AsSpan()));
        if (_ascii is not null) writer.Write(_ascii); else writer.Write(MemoryMarshal.AsBytes(_unicode.AsSpan()));
    }
}
