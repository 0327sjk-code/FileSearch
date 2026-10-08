// 文件用途：查询正确性、缓存、USN 解析、设备范围及性能回归。
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using FlashFind.Core;
using FlashFind.Core.Indexing;
using FlashFind.Core.Search;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Services;

namespace FlashFind.Verification;

internal sealed partial class CheckSuite(string output)
{
    private readonly List<object> _checks = new();
    private readonly List<object> _benchmarks = new();
    public object Report => new { Checks = _checks, Benchmarks = _benchmarks, ForegroundWindowsOpened = 0,
        ExplorerInvoked = false, LocatorVerification = "real SHParseDisplayName PIDL + intercepted SHOpenFolderAndSelectItems dispatch" };
    private void Check(string name, bool condition)
    {
        _checks.Add(new { Name = name, Passed = condition });
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        Console.WriteLine("PASS: " + name);
    }
    private static bool Throws<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }

    public void CoreTests()
    {
        var engine = new SearchEngine();
        FileEntry[] entries = {
            FileEntry.FromPath(@"C:\项目\水面材质.uasset", false),
            FileEntry.FromPath(@"D:\测试\水面材质.uasset", false),
            FileEntry.FromPath(@"D:\水面\报告.pdf", false),
            FileEntry.FromPath(@"D:\项目\My File [1],中文&(a).txt", false),
            FileEntry.FromPath(@"D:\项目\My File Backup.txt", false),
            FileEntry.FromPath(@"D:\项目\水面素材", true)
        };
        Check("名称匹配不搜索父目录或文件内容", engine.Search(entries, SearchQuery.Parse("水面")).TotalMatches == 2);
        Check("同名不同盘均返回", engine.Search(entries, SearchQuery.Parse("水面材质.uasset")).Items.Count == 2);
        Check("默认结果仅文件", engine.Search(entries, SearchQuery.Parse("")).TotalMatches == 5);
        Check("通配符与扩展名", engine.Search(entries, SearchQuery.Parse("*.uasset ext:uasset")).TotalMatches == 2);
        Check("短中文关键词", engine.Search(entries, SearchQuery.Parse("水")).TotalMatches == 2);
        Check("带空格的名称及排除词", engine.Search(entries, SearchQuery.Parse("\"My File\" -Backup")).TotalMatches == 1);
        Check("特殊字符按字面匹配", engine.Search(entries, SearchQuery.Parse("[1],中文&(a)")).TotalMatches == 1);
        Check("硬盘过滤", engine.Search(entries, SearchQuery.Parse("水面", root: @"C:\")).TotalMatches == 1);
        Check("类别过滤", engine.Search(entries, SearchQuery.Parse("", FileCategory.Document)).TotalMatches == 3);
        Check("目录前缀边界", !PathRules.IsWithin(@"D:\CodexTool\a", @"D:\Codex") && PathRules.IsWithin(@"D:\Codex\a", @"D:\Codex"));
        Check("合并重叠索引范围", PathRules.NormalizeRoots(new[] { @"D:\Test\", @"D:\Test\Nested", @"d:\test\" }).Length == 1);
        Check("盘符根目录保持单一分隔符", PathRules.NormalizeRoots(new[] { @"C:\", @"D:\" }).SequenceEqual(new[] { @"C:\", @"D:\" }));
        Check("不完整引号得到明确错误", Throws<ArgumentException>(() => SearchQuery.Parse("\"unfinished")));
        Check("取消正在运行的查询", Throws<OperationCanceledException>(() => engine.Search(entries, SearchQuery.Parse(""), new CancellationToken(true))));
        Check("通配符回退", WildcardMatcher.IsMatch("ABC中文.txt", "a*?文.*") && !WildcardMatcher.IsMatch("abc.txt", "*.png"));
        var catalog = new SearchCatalog(entries);
        Check("中文候选集与全量查询一致", engine.Search(catalog, SearchQuery.Parse("水面")).Items.SequenceEqual(engine.Search(entries, SearchQuery.Parse("水面")).Items));
        foreach (string text in new[] { "水", "My File", "报告", ".txt", "材质.uasset", "*.uasset", "ext:pdf", "-水面", "My -Backup" })
            Check("连续名称索引一致: " + text, engine.Search(catalog, SearchQuery.Parse(text)).Items.SequenceEqual(engine.Search(entries, SearchQuery.Parse(text)).Items));
        Check("连续索引不跨名称匹配", engine.Search(catalog, SearchQuery.Parse("uassetMy")).TotalMatches == 0);

        byte[] name = Encoding.Unicode.GetBytes("鱼_BP.uasset");
        var record = new byte[60 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
        name.CopyTo(record.AsSpan(60));
        Check("USN 中文名称解析", NtfsScanner.ParseRecords(record)[0].Name == "鱼_BP.uasset");
        Check("截断的 USN 记录安全拒绝", Throws<InvalidDataException>(() => NtfsScanner.ParseRecords(record.AsSpan(0, 20))));
        var nodes = new Dictionary<ulong, MftNode> { [5] = new(5, 5, ".", true), [6] = new(6, 5, "项目", true), [7] = new(7, 6, "鱼_BP.uasset", false), [8] = new(8, 9, "循环", true), [9] = new(9, 8, "循环2", true) };
        IReadOnlyList<FileEntry> resolved = NtfsScanner.ResolvePaths(nodes, @"D:\", Array.Empty<string>());
        Check("MFT 父目录还原与循环保护", resolved.Count == 2 && resolved.Any(x => x.FullPath == @"D:\项目\鱼_BP.uasset"));
        IReadOnlyList<IndexedDrive> drives = DriveDiscovery.Discover();
        Check("本机内置硬盘被硬件识别", drives.Any(x => !x.External) && drives.Where(x => !x.External).All(x => x.HardwareChecked));
        Check("默认范围不包含 NAS、USB 或可移除设备", DriveDiscovery.InternalDrives().All(x => !x.External && x.BusType is not (4 or 7 or 9 or 12 or 13 or 20)));
        _checks.Add(new { Name = "本机磁盘探测", Drives = drives });
        Check("正文对比度", Palette.Contrast(Palette.ToColor(.27, .015, 150), Palette.ToColor(1, 0, 0)) >= 7);
        Check("次要文字对比度", Palette.Contrast(Palette.ToColor(.49, .018, 150), Palette.ToColor(.973, .004, 150)) >= 4.5);
        Check("主按钮对比度", Palette.Contrast(Palette.ToColor(.4, .106, 150), Palette.ToColor(1, 0, 0)) >= 4.5);
    }

    public async Task CacheTestsAsync()
    {
        string cachePath = Path.Combine(output, "cache", "test.bin.gz");
        var cache = new CacheStore(cachePath);
        FileEntry[] entries = Enumerable.Range(0, 4097).Select(index =>
            FileEntry.FromPath(Path.Combine(output, "中文目录", $"名称_{index}, [] & .txt"), false, index, DateTime.UtcNow.Ticks)).ToArray();
        await cache.SaveAsync(entries, new[] { output });
        CachedIndex? loaded = cache.Load();
        Check("压缩索引全部字段往返", loaded is not null && loaded.Entries.SequenceEqual(entries) && loaded.Roots.SequenceEqual(new[] { output }));
        byte[] baseline = File.ReadAllBytes(cachePath);
        try { await cache.SaveAsync(entries, new[] { output }, new CancellationToken(true)); } catch (OperationCanceledException) { }
        Check("取消保存不破坏原缓存", baseline.SequenceEqual(File.ReadAllBytes(cachePath)));
        File.WriteAllBytes(cachePath, new byte[] { 1, 2, 3, 4 });
        Check("损坏缓存被检测", Throws<InvalidDataException>(() => cache.Load()));
    }

    public void Benchmark()
    {
        const int entryCount = 1_000_000;
        var entries = new FileEntry[entryCount];
        for (int index = 0; index < entryCount; index++)
            entries[index] = FileEntry.FromPath($@"D:\基准样本\目录_{index % 2000}\{(index % 200 == 0 ? "水面材质" : "ProjectAsset")}_{index:0000000}.uasset", false);
        var engine = new SearchEngine();
        var catalog = new SearchCatalog(entries);
        string[] queries = { "水面", "ProjectAsset_0000042", ".uasset", "不存在的文件" };
        foreach (string text in queries)
        {
            var query = SearchQuery.Parse(text);
            _ = engine.Search(catalog, query);
            var timings = new List<double>();
            SearchResponse? response = null;
            for (int iteration = 0; iteration < 7; iteration++)
            { response = engine.Search(catalog, query); timings.Add(response.ElapsedMilliseconds); }
            timings.Sort();
            _benchmarks.Add(new { Kind = "synthetic", Entries = entryCount, Query = text, Matches = response!.TotalMatches,
                Returned = response.Items.Count, MedianMs = Math.Round(timings[3], 2), P95Ms = Math.Round(timings[^1], 2) });
            Check($"百万索引 {text} 不截断结果", response.Items.Count == response.TotalMatches);
            if (text == "水面") Check("百万索引返回全部 5000 个中文匹配", response.TotalMatches == 5000);
        }
    }
}
