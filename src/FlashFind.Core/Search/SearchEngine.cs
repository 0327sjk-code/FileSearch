// 文件用途：内存检索、结果排序和取消；不在查询时遍历磁盘。
using System.Diagnostics;

namespace FlashFind.Core.Search;

public sealed record SearchResponse(IReadOnlyList<FileEntry> Items, int TotalMatches,
    int IndexedCount, double ElapsedMilliseconds);

public sealed class SearchEngine
{
    public SearchResponse Search(SearchCatalog catalog, SearchQuery query, CancellationToken cancellation = default)
    {
        return SearchCore(catalog.Candidates(query, cancellation), catalog.Entries.Length, query, cancellation);
    }
    public SearchResponse Search(FileEntry[] entries, SearchQuery query, CancellationToken cancellation = default)
        => SearchCore(entries, entries.Length, query, cancellation);

    private static SearchResponse SearchCore(IEnumerable<FileEntry> entries, int indexedCount, SearchQuery query, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        // 按名称匹配程度分桶，保留全部命中；避免大集合整体排序。
        var buckets = new List<FileEntry>[query.Terms.Count * 4 + 1];
        int count = 0, index = 0;
        foreach (FileEntry entry in entries)
        {
            if ((index++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            if (query.Category == FileCategory.All && entry.IsDirectory) continue;
            if (query.Category != FileCategory.All && entry.Category != query.Category) continue;
            if (query.Root is not null && !PathRules.IsWithin(entry.FullPath, query.Root)) continue;
            if (query.Extensions.Length > 0 && !query.Extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase)) continue;
            int score = Match(entry, query);
            if (score < 0) continue;
            count++;
            (buckets[score] ??= new List<FileEntry>()).Add(entry);
        }
        var results = new List<FileEntry>(count);
        foreach (List<FileEntry>? bucket in buckets)
            if (bucket is not null) results.AddRange(bucket);
        watch.Stop();
        return new(results, count, indexedCount, watch.Elapsed.TotalMilliseconds);
    }

    private static int Match(FileEntry entry, SearchQuery query)
    {
        int score = 0;
        foreach (QueryTerm term in query.Terms)
        {
            bool inName = term.Wildcard ? WildcardMatcher.IsMatch(entry.NormalizedName, term.Normalized)
                : entry.NormalizedName.Contains(term.Normalized, StringComparison.Ordinal);
            bool inPath = !inName && query.IncludePath && (term.Wildcard
                ? WildcardMatcher.IsMatch(entry.FullPath, term.Text)
                : entry.FullPath.Contains(term.Text, StringComparison.OrdinalIgnoreCase));
            if (term.Excluded) { if (inName || inPath) return -1; continue; }
            if (!inName && !inPath) return -1;
            score += !inName ? 4 : entry.NormalizedName.Equals(term.Normalized, StringComparison.Ordinal) ? 0
                : entry.NormalizedName.StartsWith(term.Normalized, StringComparison.Ordinal) ? 1 : 2;
        }
        return score;
    }
}
