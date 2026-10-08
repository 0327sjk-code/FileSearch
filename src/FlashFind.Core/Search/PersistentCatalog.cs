// 文件用途：即时查询预制索引、硬盘范围过滤和仅保存命中序号的排序。
using System.Diagnostics;
using FlashFind.Core.Storage;

namespace FlashFind.Core.Search;

internal sealed class PersistentCatalog
{
    private readonly PersistentIndex _source;
    private readonly bool[]? _allowedEntries;
    public IReadOnlyList<FileEntry> Entries { get; }
    public IReadOnlyList<FileEntry> Files { get; }
    public DateTime SavedUtc => _source.SavedUtc;

    public PersistentCatalog(PersistentIndex source, string[] roots, string[] excluded, CancellationToken cancellation)
    {
        _source = source;
        if (SamePaths(source.Roots, roots) && SamePaths(RelevantExclusions(source.Excluded, roots), RelevantExclusions(excluded, roots)))
        {
            Entries = new PersistentEntries(source, source.Header.EntryCount, files: false);
            Files = new PersistentEntries(source, source.Header.FileCount, files: true); return;
        }
        var parentScope = new byte[source.Header.ParentCount];
        for (int index = 0; index < parentScope.Length; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            string parent = source.ReadParent(index).ToString();
            if (excluded.Any(root => PathRules.IsWithin(parent, root))) continue;
            bool inside = roots.Any(root => PathRules.IsWithin(parent, root));
            bool boundary = excluded.Any(root => PathRules.IsWithin(root, parent)) ||
                !inside && roots.Any(root => PathRules.IsWithin(root, parent));
            parentScope[index] = boundary ? (byte)2 : inside ? (byte)1 : (byte)0;
        }
        _allowedEntries = new bool[source.Header.EntryCount];
        var entries = new List<int>(); var files = new List<int>();
        for (int index = 0; index < source.Header.EntryCount; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            PersistentRecord record = source.ReadRecord(index);
            byte scope = parentScope[record.ParentId];
            if (scope == 0 || scope == 2 && !InScope(source, record, roots, excluded)) continue;
            _allowedEntries[index] = true;
            entries.Add(index); if (!record.IsDirectory) files.Add(index);
        }
        Entries = new PersistentEntries(source, entries.Count, files: false, entries.ToArray());
        Files = new PersistentEntries(source, files.Count, files: true, files.ToArray());
        GC.KeepAlive(source);
    }

    private static bool InScope(PersistentIndex source, PersistentRecord record, string[] roots, string[] excluded)
    {
        bool included = false;
        foreach (string root in roots)
            if (PathRules.IsWithin(source.ReadParent(record.ParentId), source.ReadName(record), root)) { included = true; break; }
        if (!included) return false;
        foreach (string root in excluded)
            if (PathRules.IsWithin(source.ReadParent(record.ParentId), source.ReadName(record), root)) return false;
        return true;
    }

    private static bool SamePaths(string[] first, string[] second) => first.Length == second.Length &&
        first.All(path => second.Contains(path, StringComparer.OrdinalIgnoreCase));
    private static string[] RelevantExclusions(string[] excluded, string[] roots) => excluded.Where(path =>
        roots.Any(root => PathRules.IsWithin(path, root) || PathRules.IsWithin(root, path))).ToArray();

    public IEnumerable<FileEntry> Candidates(SearchQuery query, CancellationToken cancellation)
    {
        foreach (int index in _source.Candidates(query, cancellation))
            if (_allowedEntries is null || _allowedEntries[index]) yield return _source.GetEntry(index);
    }

    public SearchResponse Search(SearchQuery query, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (query.Terms.Count == 0 && query.Extensions.Length == 0 && query.Category == FileCategory.All && query.Root is null)
            return new(Files, Files.Count, Entries.Count, 0);
        var watch = Stopwatch.StartNew(); var buckets = new List<int>?[query.Terms.Count * 4 + 1];
        int examined = 0;
        try
        {
            foreach (int index in _source.Candidates(query, cancellation))
            {
                if ((examined++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                PersistentRecord record = _source.ReadRecord(index);
                if (_allowedEntries is not null && !_allowedEntries[index]) continue;
                if (query.Category == FileCategory.All ? record.IsDirectory : record.Category != query.Category) continue;
                if (query.Root is not null && !PathRules.IsWithin(_source.ReadParent(record.ParentId), _source.ReadName(record), query.Root)) continue;
                if (query.Extensions.Length > 0 && (record.IsDirectory || !QueryMatcher.HasExtension(_source.ReadName(record), query.Extensions))) continue;
                string? path = query.IncludePath ? _source.GetEntry(index).FullPath : null;
                int score = QueryMatcher.Match(_source.ReadName(record), record.Unicode, path.AsSpan(), query);
                if (score >= 0) (buckets[score] ??= new List<int>()).Add(index);
            }
            var results = new PersistentRankedEntries(_source, buckets); watch.Stop();
            return new(results, results.Count, Entries.Count, watch.Elapsed.TotalMilliseconds);
        }
        finally { GC.KeepAlive(_source); }
    }
}
