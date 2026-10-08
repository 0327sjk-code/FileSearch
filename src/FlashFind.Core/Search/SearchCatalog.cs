// 文件用途：不可变索引目录；中文查询直接筛选含 Unicode 的名称，减少候选集。
namespace FlashFind.Core.Search;

public sealed class SearchCatalog
{
    private const int PageEntryCount = 4096;
    private readonly NamePage[]? _pages;
    private readonly NamePage[]? _unicodePages;
    public static SearchCatalog Empty { get; } = new(Array.Empty<FileEntry>());
    public FileEntry[] Entries { get; }
    public FileEntry[] UnicodeEntries { get; }
    public bool HasFastLookup => _pages is not null;
    public SearchCatalog(FileEntry[] entries, bool buildFastLookup = true)
    {
        Entries = entries;
        UnicodeEntries = entries.Where(x => x.HasUnicodeName).ToArray();
        if (buildFastLookup)
        { _pages = CreatePages(Entries); _unicodePages = CreatePages(UnicodeEntries); }
    }
    public IEnumerable<FileEntry> Candidates(SearchQuery query, CancellationToken cancellation = default)
    {
        bool unicode = query.Terms.Any(x => !x.Excluded && x.RequiresUnicode) && !query.IncludePath;
        QueryTerm? seed = query.IncludePath ? null : query.Terms.Where(x => !x.Excluded && !x.Wildcard)
            .MaxBy(x => x.Normalized.Length);
        NamePage[]? pages = unicode ? _unicodePages : _pages;
        if (seed is null || pages is null) return unicode ? UnicodeEntries : Entries;
        return Lookup(pages, seed.Normalized, cancellation);
    }

    private static IEnumerable<FileEntry> Lookup(NamePage[] pages, string seed, CancellationToken cancellation)
    {
        foreach (NamePage page in pages)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (FileEntry entry in page.Find(seed)) yield return entry;
        }
    }

    private static NamePage[] CreatePages(FileEntry[] entries)
    {
        var pages = new NamePage[(entries.Length + PageEntryCount - 1) / PageEntryCount];
        for (int index = 0; index < pages.Length; index++)
        {
            int start = index * PageEntryCount;
            pages[index] = new(entries, start, Math.Min(PageEntryCount, entries.Length - start));
        }
        return pages;
    }
}
