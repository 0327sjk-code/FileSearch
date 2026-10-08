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
        if (query.Terms.Count == 0 && query.Extensions.Length == 0 && query.Category == FileCategory.All && query.Root is null)
            return new(catalog.Files, catalog.Files.Count, catalog.Entries.Length, 0);
        return SearchCore(catalog.Candidates(query, cancellation), catalog.Entries.Length, query, cancellation);
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
            if (query.Extensions.Length > 0 && !HasExtension(entry, query.Extensions)) continue;
            int score = Match(entry, query);
            if (score < 0) continue;
            count++;
            (buckets[score] ??= new List<FileEntry>()).Add(entry);
        }
        var results = new RankedEntries(buckets);
        watch.Stop();
        return new(results, count, indexedCount, watch.Elapsed.TotalMilliseconds);
    }

    private static int Match(FileEntry entry, SearchQuery query)
    {
        if (query.Terms.Count == 0) return 0;
        // 按候选项临时折叠大小写，保持与名称页一致；不为全量索引常驻另一份名称。
        string normalizedName = entry.HasUnicodeName ? entry.Name.ToLowerInvariant() : entry.Name;
        StringComparison comparison = entry.HasUnicodeName ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int score = 0;
        foreach (QueryTerm term in query.Terms)
        {
            bool inName = term.Wildcard ? WildcardMatcher.IsMatch(normalizedName, term.Normalized)
                : (!term.RequiresUnicode || entry.HasUnicodeName) && normalizedName.Contains(term.Normalized, comparison);
            bool inPath = !inName && query.IncludePath && (term.Wildcard
                ? WildcardMatcher.IsMatch(entry.FullPath, term.Text)
                : entry.FullPath.Contains(term.Text, StringComparison.OrdinalIgnoreCase));
            if (term.Excluded) { if (inName || inPath) return -1; continue; }
            if (!inName && !inPath) return -1;
            score += !inName ? 4 : normalizedName.Equals(term.Normalized, comparison) ? 0
                : normalizedName.StartsWith(term.Normalized, comparison) ? 1 : 2;
        }
        return score;
    }

    private static bool HasExtension(FileEntry entry, string[] extensions)
    {
        if (entry.IsDirectory) return false;
        ReadOnlySpan<char> extension = Path.GetExtension(entry.Name.AsSpan());
        foreach (string candidate in extensions)
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
