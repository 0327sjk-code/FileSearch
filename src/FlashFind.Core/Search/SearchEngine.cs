// 文件用途：内存检索、结果排序和取消；不在查询时遍历磁盘。
using System.Diagnostics;

namespace FlashFind.Core.Search;

public sealed record SearchResponse(IReadOnlyList<FileEntry> Items, int TotalMatches,
    int IndexedCount, double ElapsedMilliseconds);

public sealed class SearchEngine
{
    public SearchResponse Search(SearchCatalog catalog, SearchQuery query, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (catalog.Persistent is not null) return catalog.Persistent.Search(query, cancellation);
        if (query.Terms.Count == 0 && query.Extensions.Length == 0 && query.Category == FileCategory.All && query.Root is null)
            return new(catalog.Files, catalog.Files.Count, catalog.Entries.Count, 0);
        return SearchCore(catalog.Candidates(query, cancellation), catalog.Entries.Count, query, cancellation);
    }
    public SearchResponse Search(FileEntry[] entries, SearchQuery query, CancellationToken cancellation = default)
        => SearchCore(entries, entries.Length, query, cancellation);

    private static SearchResponse SearchCore(IEnumerable<FileEntry> entries, int indexedCount, SearchQuery query, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        // 按名称匹配程度分桶，保留全部命中；避免大集合整体排序。
        var buckets = new List<FileEntry>?[query.Terms.Count * 4 + 1];
        int count = 0, index = 0;
        foreach (FileEntry entry in entries)
        {
            if ((index++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            if (query.Category == FileCategory.All && entry.IsDirectory) continue;
            if (query.Category != FileCategory.All && entry.Category != query.Category) continue;
            if (query.Root is not null && !PathRules.IsWithin(entry, query.Root)) continue;
            if (query.Extensions.Length > 0 && (entry.IsDirectory || !QueryMatcher.HasExtension(entry.Name, query.Extensions))) continue;
            int score = QueryMatcher.Match(entry.Name, entry.HasUnicodeName, query.IncludePath ? entry.FullPath : null, query);
            if (score < 0) continue;
            count++;
            (buckets[score] ??= new List<FileEntry>()).Add(entry);
        }
        var results = new RankedEntries(buckets);
        watch.Stop();
        return new(results, count, indexedCount, watch.Elapsed.TotalMilliseconds);
    }

}
