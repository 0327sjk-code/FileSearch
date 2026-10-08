// 文件用途：直接查询索引的版本、边界及头部校验；缓存与查询共用格式定义。
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FlashFind.Core.Storage;

internal readonly record struct SourceStamp(long Length, long ModifiedTicks)
{
    public static SourceStamp Read(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks);
    }
}

internal readonly record struct ReadyIndexHeader(int EntryCount, int FileCount, int ParentCount, int PageCount,
    long SavedTicks, SourceStamp Source, long RecordsOffset, long FilesOffset, long ParentsOffset,
    long StringsOffset, long PagesOffset, long FileLength);

internal static class ReadyIndexFormat
{
    public const int HeaderSize = 160;
    public const int EntrySize = 40;
    public const int ParentSize = 16;
    public const int PageEntryCount = 4096;
    public const int MaximumEntries = 20_000_000;
    public const int MaximumPathLength = 32767;
    private const ulong Magic = 0x3159445249464646;
    private const int Version = 1;
    private const int ChecksumOffset = 112;

    public static long Align(long offset) => checked((offset + 7) & ~7L);

    public static void WriteHeader(Stream file, ReadyIndexHeader header)
    {
        var buffer = new byte[HeaderSize];
        using (var writer = new BinaryWriter(new MemoryStream(buffer)))
        {
            writer.Write(Magic); writer.Write(Version); writer.Write(HeaderSize);
            writer.Write(header.EntryCount); writer.Write(header.FileCount);
            writer.Write(header.ParentCount); writer.Write(header.PageCount);
            writer.Write(header.SavedTicks); writer.Write(header.Source.Length); writer.Write(header.Source.ModifiedTicks);
            writer.Write((long)HeaderSize); writer.Write(header.RecordsOffset); writer.Write(header.FilesOffset);
            writer.Write(header.ParentsOffset); writer.Write(header.StringsOffset); writer.Write(header.PagesOffset);
            writer.Write(header.FileLength);
        }
        SHA256.HashData(buffer.AsSpan(0, ChecksumOffset), buffer.AsSpan(ChecksumOffset, 32));
        file.Position = 0; file.Write(buffer);
    }

    public static ReadyIndexHeader ReadHeader(Stream file)
    {
        Span<byte> buffer = stackalloc byte[HeaderSize]; file.ReadExactly(buffer);
        if (BinaryPrimitives.ReadUInt64LittleEndian(buffer) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(buffer[8..]) != Version ||
            BinaryPrimitives.ReadInt32LittleEndian(buffer[12..]) != HeaderSize ||
            !SHA256.HashData(buffer[..ChecksumOffset]).AsSpan().SequenceEqual(buffer.Slice(ChecksumOffset, 32)))
            throw new InvalidDataException("直接查询索引的头部损坏或版本不兼容。");
        using var reader = new BinaryReader(new MemoryStream(buffer.ToArray()));
        reader.BaseStream.Position = 16;
        int entries = reader.ReadInt32(), files = reader.ReadInt32(), parents = reader.ReadInt32(), pages = reader.ReadInt32();
        long saved = reader.ReadInt64(); var stamp = new SourceStamp(reader.ReadInt64(), reader.ReadInt64());
        if (reader.ReadInt64() != HeaderSize) throw new InvalidDataException("索引元数据位置异常。");
        var header = new ReadyIndexHeader(entries, files, parents, pages, saved, stamp, reader.ReadInt64(),
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
        if (entries is < 0 or > MaximumEntries || files < 0 || files > entries || parents < 0 || parents > entries ||
            pages < 0 || pages > (entries + PageEntryCount - 1) / PageEntryCount + 1 ||
            saved < DateTime.MinValue.Ticks || saved > DateTime.MaxValue.Ticks || header.FileLength != file.Length ||
            header.RecordsOffset < HeaderSize || header.FilesOffset != header.RecordsOffset + (long)entries * EntrySize ||
            header.ParentsOffset < header.FilesOffset + (long)files * sizeof(int) ||
            header.StringsOffset != header.ParentsOffset + (long)parents * ParentSize ||
            header.PagesOffset < header.StringsOffset || header.PagesOffset > file.Length)
            throw new InvalidDataException("直接查询索引的范围异常。");
        return header;
    }
}
