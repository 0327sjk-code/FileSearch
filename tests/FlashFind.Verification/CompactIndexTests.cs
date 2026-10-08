// 文件用途：目录边界、Unicode 混合分页、缓存字段兼容和紧凑索引内存回归。
using System.IO;
using FlashFind.Core;
using FlashFind.Core.Indexing;
using FlashFind.Core.Search;

namespace FlashFind.Verification;

internal sealed partial class CheckSuite
{
    public void CompactIndexTests()
    {
        var factory = new EntryFactory();
        FileEntry first = factory.FromPath(@"D:\项目\第一个文件.uasset", false, 10, 50);
        FileEntry second = factory.FromPath(@"D:\项目\第二个文件.uasset", false);
        Check("文件及目录自身均属于精确路径", PathRules.IsWithin(first, first.FullPath) &&
            PathRules.IsWithin(factory.FromPath(@"D:\项目", true), @"D:\项目"));
        Check("紧凑路径不会串到相似目录", !PathRules.IsWithin(first, @"D:\项目2") && PathRules.IsWithin(first, @"D:\"));
        Check("路径还原保留中文与元数据", first.FullPath == @"D:\项目\第一个文件.uasset" &&
            first.Length == 10 && first.ModifiedUtcTicks == 50 && first.Extension == ".uasset");
        Check("目录尾分隔符不会重复拼接名称", FileEntry.FromPath(@"D:\项目\子目录\", true).FullPath == @"D:\项目\子目录" &&
            factory.FromPath(@"D:\项目\子目录\", true).FullPath == @"D:\项目\子目录");
        Check("大小写不敏感的文件身份", FileIdentity.Comparer.Equals(first.Identity,
            FileIdentity.FromPath(@"d:\项目\第一个文件.UASSET")));

        FileEntry[] mixed = Enumerable.Range(0, 12_500).Select(index => factory.FromPath(
            $@"D:\分页\目录_{index % 7}\{(index % 3 == 0 ? "中文" : "Asset")}_{index:00000}_ABC.txt", false)).ToArray();
        var catalog = new SearchCatalog(mixed);
        var engine = new SearchEngine();
        foreach (string term in new[] { "abc", "ABC", "_000", "中文", "asset", "ABC -001", "*123*.txt", "ext:txt", "" })
            Check("跨页分组保持全部结果及顺序: " + term, engine.Search(catalog, SearchQuery.Parse(term)).Items
                .SequenceEqual(engine.Search(mixed, SearchQuery.Parse(term)).Items));
        FileEntry[] unicode = new[] { "Σ.txt", "ς.txt", "ſ.txt", "S.txt", "K.txt", "K.txt", "İ.txt", "I.txt" }
            .Select(name => factory.FromPath(Path.Combine(@"D:\Unicode", name), false)).ToArray();
        var unicodeCatalog = new SearchCatalog(unicode);
        foreach (string term in new[] { "Σ", "ς", "ſ", "S", "K", "K", "İ", "I" })
            Check("Unicode 大小写查询与全量匹配一致: " + term, engine.Search(unicodeCatalog, SearchQuery.Parse(term)).Items
                .SequenceEqual(engine.Search(unicode, SearchQuery.Parse(term)).Items));

        Check("同目录条目复用路径字符串", ReferenceEquals(first.ParentPath, second.ParentPath));
        long before = GC.GetTotalMemory(true);
        const int count = 200_000;
        var memoryFactory = new EntryFactory();
        string parent = @"D:\内存样本\" + new string('深', 220);
        var compact = new FileEntry[count];
        for (int index = 0; index < count; index++)
            compact[index] = memoryFactory.Create(parent, $"Asset_{index % 5000:00000}.uasset", false);
        var memoryCatalog = new SearchCatalog(compact);
        long added = GC.GetTotalMemory(true) - before;
        Check("20万长路径索引及检索页小于48MiB", added < 48L * 1024 * 1024);
        Check("紧凑索引保留全部重复名称文件", engine.Search(memoryCatalog, SearchQuery.Parse(".uasset")).TotalMatches == count);
        GC.KeepAlive(memoryFactory); GC.KeepAlive(memoryCatalog);
    }
}
