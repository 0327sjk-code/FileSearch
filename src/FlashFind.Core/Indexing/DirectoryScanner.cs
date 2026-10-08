// 文件用途：普通权限目录扫描，统计不可访问目录，避免联接循环。
using System.IO.Enumeration;

namespace FlashFind.Core.Indexing;

public sealed record ScanProgress(string Root, int Indexed, int SkippedDirectories, string Mode);

public sealed class DirectoryScanner
{
    private const int ProgressBatchSize = 4096;
    private const int EnumerationBufferSize = 64 * 1024;
    private readonly record struct ScannedEntry(FileEntry Entry, bool ReparsePoint);

    public HashSet<string> Scan(string root, IReadOnlyList<string> excluded,
        Action<FileEntry> receive, Action<ScanProgress> progress, CancellationToken cancellation)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        int skipped = 0;
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (excluded.Any(path => PathRules.IsWithin(directory, path))) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false, IgnoreInaccessible = false,
                    AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = EnumerationBufferSize
                };
                var enumerable = new FileSystemEnumerable<ScannedEntry>(directory, Transform, options);
                foreach (ScannedEntry item in enumerable)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (excluded.Any(path => PathRules.IsWithin(item.Entry.FullPath, path))) continue;
                    seen.Add(item.Entry.FullPath);
                    receive(item.Entry);
                    if (item.Entry.IsDirectory && !item.ReparsePoint) pending.Push(item.Entry.FullPath);
                    if (seen.Count % ProgressBatchSize == 0)
                        progress(new(root, seen.Count, skipped, "目录索引"));
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            { skipped++; }
        }
        progress(new(root, seen.Count, skipped, "目录索引"));
        return seen;
    }

    private static ScannedEntry Transform(ref FileSystemEntry entry)
    {
        string fullPath = entry.ToFullPath();
        var file = FileEntry.FromPath(fullPath, entry.IsDirectory, entry.IsDirectory ? -1 : entry.Length,
            entry.LastWriteTimeUtc.UtcTicks);
        return new(file, (entry.Attributes & FileAttributes.ReparsePoint) != 0);
    }
}
