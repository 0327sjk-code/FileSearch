// 文件用途：冻结真实缓存的转换和启动延迟量测；取消后台扫描，不遍历用户磁盘。
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FlashFind.Core;
using FlashFind.Core.Indexing;
using FlashFind.Core.Search;
using FlashFind.Core.Storage;

namespace FlashFind.Verification;

internal static class StartupBenchmark
{
    public static string[] ReadRoots(string cachePath)
    {
        using var file = File.OpenRead(cachePath); using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (reader.ReadString() != "FLASHFIND_INDEX" || reader.ReadInt32() != 1) throw new InvalidDataException("缓存格式错误。");
        _ = reader.ReadInt64(); int count = reader.ReadInt32();
        if (count is < 0 or > 1000) throw new InvalidDataException("缓存目录数量错误。");
        return FlashFind.Core.PathRules.NormalizeRoots(Enumerable.Range(0, count).Select(_ => reader.ReadString()));
    }

    private static string[] Exclusions(string[] roots) => roots.SelectMany(root => new[]
        { Path.Combine(Path.GetPathRoot(root)!, "$Recycle.Bin"), Path.Combine(Path.GetPathRoot(root)!, "System Volume Information") })
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static async Task<int> PrepareAsync(string cachePath, string output)
    {
        Directory.CreateDirectory(output); string[] roots = ReadRoots(cachePath);
        var watch = Stopwatch.StartNew(); var cache = new CacheStore(cachePath);
        await cache.PrepareReadyAsync(roots, Exclusions(roots));
        object report = new { ConversionSeconds = watch.Elapsed.TotalSeconds, Roots = roots,
            ReadyFileBytes = new FileInfo(cache.ReadyDataPath).Length, LegacyCacheUnmodified = true };
        Write(output, "conversion.json", report); return 0;
    }

    public static async Task<int> RunAsync(string cachePath, string output)
    {
        Directory.CreateDirectory(output); string[] roots = ReadRoots(cachePath);
        using var cancellation = new CancellationTokenSource();
        var cache = new CacheStore(cachePath);
        var index = new IndexService(cache, new AppLog(Path.Combine(output, "logs")), preferNtfs: false);
        var watch = Stopwatch.StartNew(); double readyMilliseconds = 0;
        object? readyMemory = null; var queries = new List<object>();
        index.Changed += _ =>
        {
            if (!index.Catalog.IsPersistent || cancellation.IsCancellationRequested) return;
            readyMilliseconds = watch.Elapsed.TotalMilliseconds;
            var engine = new SearchEngine();
            SearchResponse all = engine.Search(index.Catalog, SearchQuery.Parse(""));
            readyMemory = Memory("打开预制索引", all.TotalMatches);
            foreach (string text in new[] { "鱼", "材质", ".uasset", "UnrealEditor", "Epic", "不存在的文件", "" })
            {
                var timings = new List<double>(); SearchResponse? response = null;
                for (int iteration = 0; iteration < 5; iteration++)
                { response = engine.Search(index.Catalog, SearchQuery.Parse(text)); timings.Add(response.ElapsedMilliseconds); }
                double firstMilliseconds = timings[0]; timings.Sort();
                FileEntry? first = response!.Items.Count > 0 ? response.Items[0] : null;
                queries.Add(new { Query = text, Matches = response.TotalMatches, MedianMilliseconds = timings[2],
                    FirstMilliseconds = firstMilliseconds, FullResults = response.Items.Count == response.TotalMatches,
                    FirstResultHasPath = first is null || Path.IsPathFullyQualified(first.FullPath) });
            }
            cancellation.Cancel();
        };
        try { await index.StartAsync(roots, cancellation.Token); } catch (OperationCanceledException) { }
        object afterSearch = Memory("全部重复查询", index.Catalog.Files.Count);
        int count = index.Catalog.Entries.Count;
        await index.DisposeAsync();
        if (readyMemory is null) throw new InvalidOperationException("未能在启动时直接查询索引。");
        Write(output, "startup-benchmark.json", new { Entries = count, ReadyMilliseconds = readyMilliseconds,
            CompleteMutableIndexLoaded = index.Snapshot.Length > 0, ReadyMemory = readyMemory, AfterQueries = afterSearch,
            Queries = queries, NoDiskScan = cancellation.IsCancellationRequested });
        return 0;
    }

    private static object Memory(string phase, int files)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { Phase = phase, Files = files, WorkingSetMiB = process.WorkingSet64 / 1048576d,
            ManagedMiB = GC.GetTotalMemory(false) / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d };
    }
    private static void Write(string output, string name, object report)
    {
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, name), json); Console.WriteLine(json);
    }
}
