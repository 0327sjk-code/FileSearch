// 文件用途：内存与磁盘索引共用筛选语义，保证大小写、排除词和排序一致。
namespace FlashFind.Core.Search;

internal static class QueryMatcher
{
    public static int Match(ReadOnlySpan<char> name, bool unicode, ReadOnlySpan<char> path, SearchQuery query)
    {
        if (query.Terms.Count == 0) return 0;
        string? normalized = unicode ? name.ToString().ToLowerInvariant() : null;
        ReadOnlySpan<char> searchName = normalized is null ? name : normalized.AsSpan();
        StringComparison comparison = unicode ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int score = 0;
        foreach (QueryTerm term in query.Terms)
        {
            bool inName = term.Wildcard ? WildcardMatcher.IsMatch(searchName, term.Normalized)
                : (!term.RequiresUnicode || unicode) && searchName.Contains(term.Normalized, comparison);
            bool inPath = !inName && query.IncludePath && (term.Wildcard
                ? WildcardMatcher.IsMatch(path, term.Text) : path.Contains(term.Text, StringComparison.OrdinalIgnoreCase));
            if (term.Excluded) { if (inName || inPath) return -1; continue; }
            if (!inName && !inPath) return -1;
            score += !inName ? 4 : searchName.Equals(term.Normalized, comparison) ? 0
                : searchName.StartsWith(term.Normalized, comparison) ? 1 : 2;
        }
        return score;
    }

    public static bool HasExtension(ReadOnlySpan<char> name, string[] extensions)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(name);
        foreach (string candidate in extensions) if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
