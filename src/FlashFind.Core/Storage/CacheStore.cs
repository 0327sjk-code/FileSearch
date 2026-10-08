// 文件用途：带版本的压缩索引、原子写入和损坏恢复。
using System.IO.Compression;
using System.Text;
using FlashFind.Core.Indexing;
using FlashFind.Core.Search;

namespace FlashFind.Core.Storage;

public sealed record CachedIndex(FileEntry[] Entries, string[] Roots, DateTime SavedUtc);
public sealed record CachedCatalog(SearchCatalog Catalog, DateTime SavedUtc);
public sealed record ReadyIndexInfo(string[] Roots, string[] Excluded, DateTime SavedUtc, int EntryCount, int FileCount, long DataBytes);

public sealed class CacheStore(string path)
{
    private const string Magic = "FLASHFIND_INDEX";
    private const int FormatVersion = 1;
    private const int MaximumEntries = 20_000_000;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string ReadyPath => Path + ".ready";
    public string ReadyDataPath => ReadyIndexPointer.Read(ReadyPath);
    public ReadyIndexInfo ReadReadyInfo() => PersistentIndex.ReadInfo(ReadyDataPath, SourceStamp.Read(Path));

    public CachedCatalog? OpenReady(string[] roots, string[]? excluded = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(ReadyPath) || !File.Exists(Path)) return null;
        string data = ReadyDataPath;
        PersistentIndex source = PersistentIndex.Open(data, SourceStamp.Read(Path));
        var catalog = new PersistentCatalog(source, roots, excluded ?? Array.Empty<string>(), cancellation);
        ReadyIndexPointer.CleanOld(ReadyPath, data);
        return new(new SearchCatalog(catalog), source.SavedUtc);
    }

    public async Task PrepareReadyAsync(string[] roots, string[]? excluded = null, CancellationToken cancellation = default)
    {
        excluded ??= Array.Empty<string>();
        await _writeLock.WaitAsync(cancellation).ConfigureAwait(false);
        string temporary = ReadyPath + "." + Guid.NewGuid().ToString("N") + ".data";
        bool published = false;
        try
        {
            await Task.Run(() =>
            {
                SourceStamp source = SourceStamp.Read(Path);
                CachedIndex cached = Load(cancellation) ?? throw new FileNotFoundException("还没有可转换的文件索引。", Path);
                FileEntry[] selected = cached.Entries.Where(entry => roots.Any(root => PathRules.IsWithin(entry, root)) &&
                    !excluded.Any(root => PathRules.IsWithin(entry, root))).ToArray();
                ReadyIndexWriter.Write(temporary, selected, roots, excluded, cached.SavedUtc, source, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (SourceStamp.Read(Path) != source) throw new IOException("转换期间原索引已变化，请重新转换。");
                ReadyIndexPointer.Publish(ReadyPath, temporary); published = true;
                ReadyIndexPointer.CleanOld(ReadyPath, temporary);
            }, cancellation).ConfigureAwait(false);
        }
        finally
        { if (!published && File.Exists(temporary)) File.Delete(temporary); _writeLock.Release(); }
    }

    public CachedIndex? Load(CancellationToken cancellation = default, EntryFactory? factory = null)
    {
        if (!File.Exists(Path)) return null;
        factory ??= new EntryFactory();
        using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (reader.ReadString() != Magic || reader.ReadInt32() != FormatVersion)
            throw new InvalidDataException("索引格式不兼容，将重新扫描。");
        var saved = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        int rootCount = reader.ReadInt32();
        if (rootCount is < 0 or > 1000) throw new InvalidDataException("索引目录数量异常。");
        var roots = new string[rootCount];
        for (int index = 0; index < roots.Length; index++) roots[index] = reader.ReadString();
        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumEntries) throw new InvalidDataException("索引条目数量异常。");
        var entries = new FileEntry[count];
        for (int index = 0; index < count; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            string fullPath = reader.ReadString();
            if (!System.IO.Path.IsPathFullyQualified(fullPath) || fullPath.Length > 32767)
                throw new InvalidDataException("索引路径异常。");
            bool directory = reader.ReadBoolean();
            long length = reader.ReadInt64(), ticks = reader.ReadInt64();
            entries[index] = factory.FromPath(fullPath, directory, length, ticks);
        }
        return new(entries, roots, saved);
    }

    public async Task SaveAsync(FileEntry[] entries, string[] roots, CancellationToken cancellation = default, string[]? excluded = null)
    {
        await _writeLock.WaitAsync(cancellation).ConfigureAwait(false);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string readyTemporary = ReadyPath + "." + Guid.NewGuid().ToString("N") + ".data";
        bool published = false;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await Task.Run(() =>
            {
                DateTime saved = DateTime.UtcNow;
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072))
                {
                    using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                    using (var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write(Magic); writer.Write(FormatVersion); writer.Write(saved.Ticks);
                        writer.Write(roots.Length);
                        foreach (string root in roots) writer.Write(root);
                        writer.Write(entries.Length);
                        for (int index = 0; index < entries.Length; index++)
                        {
                            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                            FileEntry entry = entries[index];
                            writer.Write(entry.FullPath); writer.Write(entry.IsDirectory);
                            writer.Write(entry.Length); writer.Write(entry.ModifiedUtcTicks);
                        }
                    }
                    file.Flush(flushToDisk: true);
                }
                ReadyIndexWriter.Write(readyTemporary, entries, roots, excluded ?? Array.Empty<string>(), saved,
                    SourceStamp.Read(temporary), cancellation);
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, Path, overwrite: true);
                ReadyIndexPointer.Publish(ReadyPath, readyTemporary); published = true;
                ReadyIndexPointer.CleanOld(ReadyPath, readyTemporary);
            }, cancellation).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (!published && File.Exists(readyTemporary)) File.Delete(readyTemporary);
            _writeLock.Release();
        }
    }
}
