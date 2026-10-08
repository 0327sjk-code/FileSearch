// 文件用途：直接打开索引、检索一致性、原子替换、损坏回退和启动中退出回归。
using System.Diagnostics;
using System.IO;
using FlashFind.Core;
using FlashFind.Core.Indexing;
using FlashFind.Core.Search;
using FlashFind.Core.Storage;

namespace FlashFind.Verification;

internal sealed partial class CheckSuite
{
    public async Task PersistentIndexTestsAsync()
    {
        string directory = Path.Combine(output, "direct-index"); Directory.CreateDirectory(directory);
        var cache = new CacheStore(Path.Combine(directory, "index.bin.gz"));
        var factory = new EntryFactory();
        FileEntry[] entries = Enumerable.Range(0, 20_000).Select(index => factory.FromPath(
            $@"{(index % 2 == 0 ? "D" : "F")}:\项目\目录_{index % 13}\{(index % 3 == 0 ? "中文" : "Asset")}_{index:00000}_ABC.{(index % 5 == 0 ? "png" : "txt")}", false,
            index, DateTime.UnixEpoch.Ticks + index)).Concat(new[] { "Σ.txt", "ς.txt", "ſ.txt", "S.txt", "K.txt", "K.txt", "İ.txt", "I.txt" }
                .Select(name => factory.FromPath(Path.Combine(@"D:\Unicode", name), false)))
            .Append(factory.FromPath(@"D:\项目\目录_0", true)).ToArray();
        string[] roots = { @"D:\", @"F:\" };
        await cache.SaveAsync(entries, roots);
        var watch = Stopwatch.StartNew();
        CachedCatalog direct = cache.OpenReady(roots) ?? throw new InvalidOperationException("未生成直接查询索引。");
        watch.Stop();
        Check("直接索引打开即包含全部文件", direct.Catalog.IsPersistent && direct.Catalog.Files.Count == entries.Length - 1);
        Check("打开预制索引无需生成全量条目对象", direct.Catalog.Entries is IFileIdentityLookup);
        var engine = new SearchEngine();
        foreach (string text in new[] { "", "abc", "中文", "Asset", "ABC -001", "*123*.txt", "ext:png", "中文 ext:txt", "-中文", "不存在的文件", "Σ", "ς", "ſ", "S", "K", "K", "İ", "I" })
        {
            SearchQuery query = SearchQuery.Parse(text);
            Check("磁盘与内存查询全部字段及顺序一致: " + text,
                engine.Search(direct.Catalog, query).Items.SequenceEqual(engine.Search(entries, query).Items));
        }
        foreach (SearchQuery query in new[] { SearchQuery.Parse("", FileCategory.Image),
            SearchQuery.Parse("", FileCategory.Folder), SearchQuery.Parse("ABC", root: @"F:\"),
            SearchQuery.Parse("", root: @"D:\项目\目录_0"), SearchQuery.Parse("项目", includePath: true) })
            Check("映射查询支持分类、磁盘与路径: " + query.Category + query.Root + query.IncludePath,
                engine.Search(direct.Catalog, query).Items.SequenceEqual(engine.Search(entries, query).Items));
        var scoped = cache.OpenReady(new[] { @"F:\" })!;
        Check("旧索引立即按新硬盘范围筛选", engine.Search(scoped.Catalog, SearchQuery.Parse("" )).Items
            .SequenceEqual(engine.Search(entries, SearchQuery.Parse("", root: @"F:\")).Items));
        var exactScope = cache.OpenReady(new[] { @"D:\项目\目录_0" })!;
        Check("缩小索引范围保留根目录自身及全部子项", exactScope.Catalog.Entries.SequenceEqual(entries
            .Where(entry => PathRules.IsWithin(entry, @"D:\项目\目录_0"))));
        var excludedScope = cache.OpenReady(roots, new[] { entries[0].FullPath, @"F:\项目\目录_1" })!;
        Check("变更排除范围不泄漏目录自身或精确文件", excludedScope.Catalog.Entries.SequenceEqual(entries
            .Where(entry => !PathRules.IsWithin(entry, entries[0].FullPath) && !PathRules.IsWithin(entry, @"F:\项目\目录_1"))));
        FileEntry selected = direct.Catalog.Files[321];
        var lookup = (IFileIdentityLookup)direct.Catalog.Files;
        Check("延迟条目的路径定位无需引用同一对象", lookup.FindIdentity(FileIdentity.FromPath(selected.FullPath.ToUpperInvariant())) == 321);
        SearchResponse ranked = engine.Search(direct.Catalog, SearchQuery.Parse("ABC"));
        Check("磁盘排序结果支持保持选中", ((IFileIdentityLookup)ranked.Items).FindIdentity(ranked.Items[10].Identity) == 10);
        Check("映射检索可立即取消", Throws<OperationCanceledException>(() =>
            engine.Search(direct.Catalog, SearchQuery.Parse("abc"), new CancellationToken(true))));
        var rows = new FlashFind.Desktop.ViewModels.ResultList(direct.Catalog.Files);
        object oldRow = rows[0]!;
        Check("虚拟结果行按序号直接定位", rows.IndexOf(oldRow) == 0);
        rows.Update(ranked.Items);
        Check("更新结果不会保留上一轮选中行", rows.IndexOf(oldRow) == -1 && rows.Count == ranked.TotalMatches);
        object retained = rows[0]!;
        for (int index = 0; index < 5000; index++) _ = rows[index];
        Check("行缓存淘汰后仍能定位当前行", rows.IndexOf(retained) == 0);
        await cache.SaveAsync(new[] { entries[0] }, roots);
        Check("替换缓存时旧结果仍然可读", direct.Catalog.Files[321] == selected);
        Check("下一次打开采用最新原子保存索引", cache.OpenReady(roots)!.Catalog.Files.Count == 1);
        byte[] stable = File.ReadAllBytes(cache.ReadyPath);
        try { await cache.SaveAsync(entries, roots, new CancellationToken(true)); } catch (OperationCanceledException) { }
        Check("取消写入保留直接查询缓存", stable.SequenceEqual(File.ReadAllBytes(cache.ReadyPath)));
        byte[] broken = stable.ToArray(); broken[32] ^= 1; File.WriteAllBytes(cache.ReadyPath, broken);
        Check("映射缓存头部损坏被检测", Throws<InvalidDataException>(() => cache.OpenReady(roots)));
        await cache.PrepareReadyAsync(roots);
        using (var corruptHeader = new FileStream(cache.ReadyDataPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        { corruptHeader.Position = 32; corruptHeader.WriteByte(127); }
        Check("数据索引头部校验拒绝改坏的字段", Throws<InvalidDataException>(() => cache.OpenReady(roots)));
        await cache.PrepareReadyAsync(roots);
        Check("旧缓存兼容转换不丢失字段", cache.OpenReady(roots)!.Catalog.Files[0] == entries[0]);
        _benchmarks.Add(new { Dataset = "direct-index", Entries = entries.Length, OpenMilliseconds = watch.Elapsed.TotalMilliseconds });

        await CheckCachedStartupAsync(directory);
    }

    private async Task CheckCachedStartupAsync(string directory)
    {
        string root = Path.Combine(directory, "startup-root"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "启动即可搜索.txt"), "fixture");
        var cache = new CacheStore(Path.Combine(directory, "startup-cache.bin.gz"));
        FileEntry entry = FileEntry.FromPath(Path.Combine(root, "启动即可搜索.txt"), false);
        string[] roots = PathRules.NormalizeRoots(new[] { root });
        string[] excluded = { Path.Combine(Path.GetPathRoot(root)!, "$Recycle.Bin"),
            Path.Combine(Path.GetPathRoot(root)!, "System Volume Information") };
        await cache.SaveAsync(new[] { entry }, roots, excluded: excluded);
        var index = new IndexService(cache, new AppLog(Path.Combine(directory, "logs")), preferNtfs: false);
        bool directWasSearchable = false;
        index.Changed += _ =>
        {
            if (index.Catalog.IsPersistent)
                directWasSearchable = index.Snapshot.Length == 0 && new SearchEngine().Search(index.Catalog,
                    SearchQuery.Parse("启动即可搜索")).TotalMatches == 1;
        };
        await index.StartAsync(roots);
        Check("普通启动在完整加载前已经可查询缓存", directWasSearchable);
        await index.DisposeAsync();
        File.WriteAllBytes(cache.ReadyPath, new byte[] { 1, 2, 3 });
        var fallback = new IndexService(cache, new AppLog(Path.Combine(directory, "fallback-logs")), preferNtfs: false);
        await fallback.StartAsync(roots);
        Check("直接索引损坏时兼容缓存及后台重建恢复", new SearchEngine().Search(fallback.Catalog,
            SearchQuery.Parse("启动即可搜索")).TotalMatches == 1 && cache.OpenReady(roots, excluded) is not null);
        await fallback.DisposeAsync();
    }
}
