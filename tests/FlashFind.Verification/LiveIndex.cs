// 文件用途：可限时的真实磁盘后台索引与查询性能记录。
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlashFind.Core.Indexing;
using FlashFind.Core.Search;
using FlashFind.Core.Storage;

namespace FlashFind.Verification;

internal static class LiveIndex
{
    public static async Task<int> RunAsync(string[] args)
    {
        string directory = args[0]; Directory.CreateDirectory(directory);
        int seconds = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        string[] roots = args.Length > 2 ? args[2..] : AppSettings.DefaultRoots();
        var log = new AppLog(Path.Combine(directory, "logs"));
        await using var index = new IndexService(new CacheStore(Path.Combine(directory, "index.bin.gz")), log, new[] { directory });
        DateTime lastPrinted = DateTime.MinValue;
        index.Changed += state =>
        { if ((DateTime.UtcNow - lastPrinted).TotalSeconds >= 10 || !state.IsScanning) { lastPrinted = DateTime.UtcNow; Console.WriteLine($"{DateTime.Now:HH:mm:ss} {state.Count} {state.Message}"); } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var watch = Stopwatch.StartNew();
        await index.StartAsync(roots, timeout.Token);
        var engine = new SearchEngine();
        var searches = new List<object>();
        foreach (string query in new[] { "鱼", "材质", "闪寻", ".uasset", "UnrealEditor" })
        {
            SearchQuery parsed = SearchQuery.Parse(query);
            _ = engine.Search(index.Catalog, parsed);
            var times = new List<double>();
            SearchResponse result = engine.Search(index.Catalog, parsed);
            for (int iteration = 0; iteration < 5; iteration++) { result = engine.Search(index.Catalog, parsed); times.Add(result.ElapsedMilliseconds); }
            times.Sort();
            searches.Add(new { Query = query, Matches = result.TotalMatches, Returned = result.Items.Count,
                MedianMilliseconds = Math.Round(times[2], 2) });
        }
        object report = new { Roots = roots, Entries = index.Snapshot.Length, ScanningSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2),
            Completed = !timeout.IsCancellationRequested, State = index.State, Searches = searches };
        File.WriteAllText(Path.Combine(directory, "live-benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    public static int BenchmarkCache(string path, string output)
    {
        CachedIndex cached = new CacheStore(path).Load() ?? throw new FileNotFoundException("索引不存在。");
        var watch = Stopwatch.StartNew();
        var catalog = new SearchCatalog(cached.Entries);
        double buildMilliseconds = watch.Elapsed.TotalMilliseconds;
        var engine = new SearchEngine();
        var measurements = new List<object>();
        foreach (string query in new[] { "鱼", "材质", ".uasset", "UnrealEditor", "Epic", "不存在的文件", "" })
        {
            SearchQuery parsed = SearchQuery.Parse(query);
            _ = engine.Search(catalog, parsed);
            var times = new List<double>();
            SearchResponse result = engine.Search(catalog, parsed);
            for (int index = 0; index < 7; index++)
            { result = engine.Search(catalog, parsed); times.Add(result.ElapsedMilliseconds); }
            times.Sort();
            measurements.Add(new { Query = query, Matches = result.TotalMatches, Returned = result.Items.Count,
                MedianMilliseconds = Math.Round(times[3], 2), P95Milliseconds = Math.Round(times[^1], 2) });
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "real-search-benchmark.json"), JsonSerializer.Serialize(
            new { Entries = cached.Entries.Length, Roots = cached.Roots, CatalogBuildMilliseconds = Math.Round(buildMilliseconds, 2),
                Dataset = "real partial disk filename index; cached from bounded background scan", Measurements = measurements },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
