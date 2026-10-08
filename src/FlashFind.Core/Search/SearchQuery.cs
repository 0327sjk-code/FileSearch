// 文件用途：解析关键词、引号、排除词、扩展名及通配符。
using System.Text;

namespace FlashFind.Core.Search;

public sealed record QueryTerm(string Text, bool Excluded, bool Wildcard)
{
    public string Normalized { get; } = Text.ToLowerInvariant();
    public bool RequiresUnicode { get; } = Text.ToLowerInvariant().AsSpan().IndexOfAnyExceptInRange('\0', '\u007f') >= 0;
}
public sealed record SearchQuery(IReadOnlyList<QueryTerm> Terms, string[] Extensions,
    FileCategory Category, string? Root, bool IncludePath)
{
    public const int MaximumQueryLength = 512;

    public static SearchQuery Parse(string text, FileCategory category = FileCategory.All,
        string? root = null, bool includePath = false)
    {
        if (text.Length > MaximumQueryLength)
            throw new ArgumentException($"搜索词不能超过 {MaximumQueryLength} 个字符。");
        if (text.Contains('\0')) throw new ArgumentException("文件名不能包含空字符。");
        var terms = new List<QueryTerm>();
        var extensions = new List<string>();
        foreach (string part in Split(text))
        {
            if (part.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            {
                extensions.AddRange(part[4..].Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => "." + x.TrimStart('.').ToLowerInvariant()));
                continue;
            }
            bool excluded = part.StartsWith('-') && part.Length > 1;
            string token = excluded ? part[1..] : part;
            terms.Add(new(token, excluded, token.Contains('*') || token.Contains('?')));
        }
        return new(terms, extensions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            category, string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root), includePath);
    }

    private static IEnumerable<string> Split(string text)
    {
        var token = new StringBuilder();
        bool quoted = false;
        foreach (char character in text)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (token.Length > 0) { yield return token.ToString(); token.Clear(); }
            }
            else token.Append(character);
        }
        if (quoted) throw new ArgumentException("搜索词中的双引号需要成对出现。");
        if (token.Length > 0) yield return token.ToString();
    }
}

public static class WildcardMatcher
{
    public static bool IsMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int textIndex = 0, patternIndex = 0, starIndex = -1, retryIndex = 0;
        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' ||
                char.ToUpperInvariant(text[textIndex]) == char.ToUpperInvariant(pattern[patternIndex])))
            { textIndex++; patternIndex++; }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            { starIndex = patternIndex++; retryIndex = textIndex; }
            else if (starIndex >= 0) { patternIndex = starIndex + 1; textIndex = ++retryIndex; }
            else return false;
        }
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }
}
