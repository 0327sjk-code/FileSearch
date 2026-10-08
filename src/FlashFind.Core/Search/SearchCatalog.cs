// 文件用途：不可变搜索目录；名称只打包一次，分组查询并按原条目顺序合并。
using System.Text;
namespace FlashFind.Core.Search;

public sealed class SearchCatalog
{
    private const int PageEntryCount = 4096;
    private readonly int[] _unicodeIndices;
    private readonly NamePage[]? _asciiPages;
    private readonly NamePage[]? _unicodePages;
    public static SearchCatalog Empty { get; } = new(Array.Empty<FileEntry>());
    internal PersistentCatalog? Persistent { get; }
    public IReadOnlyList<FileEntry> Entries { get; }
    public IReadOnlyList<FileEntry> Files { get; }
    public bool HasFastLookup => Persistent is not null || _asciiPages is not null;
    public bool IsPersistent => Persistent is not null;
    internal SearchCatalog(PersistentCatalog source)
    { Persistent = source; Entries = source.Entries; Files = source.Files; _unicodeIndices = Array.Empty<int>(); }
    public SearchCatalog(FileEntry[] entries, bool buildFastLookup = true)
    {
        Entries = entries;
        int unicodeCount = entries.Count(entry => entry.HasUnicodeName);
        _unicodeIndices = new int[unicodeCount];
        var fileIndices = new int[entries.Count(entry => !entry.IsDirectory)];
        int[]? asciiIndices = buildFastLookup ? new int[entries.Length - unicodeCount] : null;
        int unicode = 0, ascii = 0, files = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            if (!entries[index].IsDirectory) fileIndices[files++] = index;
            if (entries[index].HasUnicodeName) _unicodeIndices[unicode++] = index;
            else if (asciiIndices is not null) asciiIndices[ascii++] = index;
        }
        Files = new IndexedEntries(entries, fileIndices);
        if (asciiIndices is not null)
        {
            _asciiPages = CreatePages(entries, asciiIndices, ascii: true);
            _unicodePages = CreatePages(entries, _unicodeIndices, ascii: false);
        }
    }
    public IEnumerable<FileEntry> Candidates(SearchQuery query, CancellationToken cancellation = default)
    {
        if (Persistent is not null) return Persistent.Candidates(query, cancellation);
        bool unicode = query.Terms.Any(x => !x.Excluded && x.RequiresUnicode) && !query.IncludePath;
        QueryTerm? seed = query.IncludePath ? null : query.Terms.Where(x => !x.Excluded && !x.Wildcard)
            .MaxBy(x => x.Normalized.Length);
        if (seed is null || !HasFastLookup) return unicode ? Materialize(_unicodeIndices) : Entries;
        byte[]? asciiSeed = seed.Normalized.AsSpan().IndexOfAnyExceptInRange('\0', '\u007f') < 0
            ? Encoding.ASCII.GetBytes(seed.Normalized) : null;
        IEnumerable<int> indices = unicode
            ? Lookup(_unicodePages!, seed.Normalized, asciiSeed, cancellation)
            : Merge(Lookup(_asciiPages!, seed.Normalized, asciiSeed, cancellation),
                Lookup(_unicodePages!, seed.Normalized, asciiSeed, cancellation));
        return Materialize(indices);
    }

    private IEnumerable<FileEntry> Materialize(IEnumerable<int> indices)
    { foreach (int index in indices) yield return Entries[index]; }

    private static IEnumerable<int> Lookup(NamePage[] pages, string seed, byte[]? asciiSeed, CancellationToken cancellation)
    {
        foreach (NamePage page in pages)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (int index in page.Find(seed, asciiSeed)) yield return index;
        }
    }

    internal static IEnumerable<int> Merge(IEnumerable<int> left, IEnumerable<int> right)
    {
        using IEnumerator<int> first = left.GetEnumerator(), second = right.GetEnumerator();
        bool hasFirst = first.MoveNext(), hasSecond = second.MoveNext();
        while (hasFirst || hasSecond)
        {
            if (!hasSecond || hasFirst && first.Current < second.Current)
            { yield return first.Current; hasFirst = first.MoveNext(); }
            else { yield return second.Current; hasSecond = second.MoveNext(); }
        }
    }

    private static NamePage[] CreatePages(FileEntry[] entries, int[] indices, bool ascii)
    {
        var pages = new NamePage[(indices.Length + PageEntryCount - 1) / PageEntryCount];
        for (int index = 0; index < pages.Length; index++)
        {
            int start = index * PageEntryCount;
            pages[index] = new(entries, indices, start, Math.Min(PageEntryCount, indices.Length - start), ascii);
        }
        return pages;
    }
}
