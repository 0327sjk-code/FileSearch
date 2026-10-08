// 文件用途：将共享目录、固定记录和检索名称页写成可直接打开的索引。
using System.Runtime.InteropServices;
using System.Text;
using FlashFind.Core.Search;

namespace FlashFind.Core.Storage;

internal static class ReadyIndexWriter
{
    public static void Write(string path, FileEntry[] entries, string[] roots, string[] excluded,
        DateTime saved, SourceStamp source, CancellationToken cancellation)
    {
        var parents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int unicodeCount = 0, fileCount = 0;
        long nameBytes = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            FileEntry entry = entries[index];
            if (!parents.ContainsKey(entry.ParentPath)) parents.Add(entry.ParentPath, parents.Count);
            if (entry.HasUnicodeName) unicodeCount++;
            if (!entry.IsDirectory) fileCount++;
            nameBytes = checked(nameBytes + (long)entry.Name.Length * sizeof(char));
        }
        string[] parentPaths = parents.Keys.ToArray();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
        file.SetLength(ReadyIndexFormat.HeaderSize); file.Position = ReadyIndexFormat.HeaderSize;
        WritePaths(writer, roots); WritePaths(writer, excluded); Pad(writer);
        long recordsOffset = file.Position;
        long filesOffset = checked(recordsOffset + (long)entries.Length * ReadyIndexFormat.EntrySize);
        long parentsOffset = ReadyIndexFormat.Align(filesOffset + (long)fileCount * sizeof(int));
        long stringsOffset = checked(parentsOffset + (long)parents.Count * ReadyIndexFormat.ParentSize);
        long namesOffset = stringsOffset + parentPaths.Sum(parent => (long)parent.Length * sizeof(char));
        long pagesOffset = ReadyIndexFormat.Align(checked(namesOffset + nameBytes));
        long nameOffset = namesOffset;
        var asciiIndices = new int[entries.Length - unicodeCount]; var unicodeIndices = new int[unicodeCount];
        int ascii = 0, unicode = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            FileEntry entry = entries[index];
            int flags = (entry.IsDirectory ? 1 : 0) | (entry.HasUnicodeName ? 2 : 0) | ((int)entry.Category << 8);
            writer.Write(parents[entry.ParentPath]); writer.Write(flags); writer.Write(nameOffset);
            writer.Write(entry.Name.Length); writer.Write(0); writer.Write(entry.Length); writer.Write(entry.ModifiedUtcTicks);
            nameOffset += (long)entry.Name.Length * sizeof(char);
            if (entry.HasUnicodeName) unicodeIndices[unicode++] = index; else asciiIndices[ascii++] = index;
        }
        for (int index = 0; index < entries.Length; index++) if (!entries[index].IsDirectory) writer.Write(index);
        Pad(writer);
        long parentOffset = stringsOffset;
        foreach (string parent in parentPaths)
        {
            writer.Write(parentOffset); writer.Write(parent.Length); writer.Write(0);
            parentOffset += (long)parent.Length * sizeof(char);
        }
        foreach (string parent in parentPaths) WriteChars(writer, parent);
        for (int index = 0; index < entries.Length; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            WriteChars(writer, entries[index].Name);
        }
        Pad(writer);
        int pageCount = WritePages(writer, entries, asciiIndices, true, cancellation) +
            WritePages(writer, entries, unicodeIndices, false, cancellation);
        var header = new ReadyIndexHeader(entries.Length, fileCount, parents.Count, pageCount, saved.Ticks, source,
            recordsOffset, filesOffset, parentsOffset, stringsOffset, pagesOffset, file.Length);
        ReadyIndexFormat.WriteHeader(file, header); file.Flush(flushToDisk: true);
    }

    private static int WritePages(BinaryWriter writer, FileEntry[] entries, int[] indices, bool ascii,
        CancellationToken cancellation)
    {
        int count = (indices.Length + ReadyIndexFormat.PageEntryCount - 1) / ReadyIndexFormat.PageEntryCount;
        for (int page = 0; page < count; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            int start = page * ReadyIndexFormat.PageEntryCount;
            new NamePage(entries, indices, start, Math.Min(ReadyIndexFormat.PageEntryCount, indices.Length - start), ascii)
                .Write(writer);
        }
        return count;
    }

    private static void WritePaths(BinaryWriter writer, string[] paths)
    { writer.Write(paths.Length); foreach (string path in paths) writer.Write(path); }
    private static void WriteChars(BinaryWriter writer, string value) => writer.Write(MemoryMarshal.AsBytes(value.AsSpan()));
    private static void Pad(BinaryWriter writer)
    { while (writer.BaseStream.Position != ReadyIndexFormat.Align(writer.BaseStream.Position)) writer.Write((byte)0); }
}
