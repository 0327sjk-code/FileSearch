// 文件用途：索引生命周期、缓存、增量监控、快照发布和失败恢复。
using System.Collections.Concurrent;
using System.ComponentModel;
using FlashFind.Core.Storage;
using FlashFind.Core.Search;

namespace FlashFind.Core.Indexing;

public sealed record IndexState(int Count, bool IsScanning, string Message, int SkippedDirectories,
    DateTime? LastSavedUtc, string Mode);

public sealed class IndexService : IAsyncDisposable
{
    private const int SnapshotIntervalMilliseconds = 1200;
    private const int MonitorIntervalMilliseconds = 700;
    private const int SaveIntervalSeconds = 20;
    private const int ReconcileIntervalHours = 6;
    private const int MaximumPendingEvents = 100_000;
    private sealed record FileChange(string Path, string? OldPath, bool Deleted);

    private readonly ConcurrentDictionary<string, FileEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FileChange> _changes = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CacheStore _cache;
    private readonly AppLog _log;
    private readonly string[] _excluded;
    private readonly bool _preferNtfs;
    private FileEntry[] _snapshot = Array.Empty<FileEntry>();
    private SearchCatalog _catalog = SearchCatalog.Empty;
    private long _lastReportTick;
    private Task? _maintenance;
    private CancellationTokenSource? _activeScan;
    private int _dirty, _rescanning, _pendingCount;
    private volatile bool _scanning;
    private int _skipped;
    private DateTime _lastSaved = DateTime.MinValue, _lastScan = DateTime.MinValue;
    private readonly TaskCompletionSource _cacheReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private string _message = "准备索引", _mode = "目录索引";
    public string[] Roots { get; private set; } = Array.Empty<string>();
    public FileEntry[] Snapshot => Volatile.Read(ref _snapshot);
    public SearchCatalog Catalog => Volatile.Read(ref _catalog);
    public IndexState State => new(_entries.Count, _scanning, _message, _skipped,
        _lastSaved == DateTime.MinValue ? null : _lastSaved, _mode);
    public event Action<IndexState>? Changed;

    public IndexService(CacheStore cache, AppLog log, IEnumerable<string>? excluded = null, bool preferNtfs = true)
    {
        _cache = cache; _log = log; _preferNtfs = preferNtfs;
        _excluded = (excluded ?? Array.Empty<string>()).Select(Path.GetFullPath).ToArray();
    }

    public async Task StartAsync(IEnumerable<string> roots, CancellationToken cancellation = default)
    {
        if (_started) throw new InvalidOperationException("索引服务已经启动。");
        _started = true;
        Roots = PathRules.NormalizeRoots(roots);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        try { await Task.Run(() => LoadCache(linked.Token), linked.Token).ConfigureAwait(false); }
        finally { _cacheReady.TrySetResult(); }
        StartWatchers();
        _maintenance = MaintainAsync(_lifetime.Token);
        await RebuildAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task RebuildAsync(CancellationToken cancellation = default)
    {
        if (!await _scanGate.WaitAsync(0, cancellation).ConfigureAwait(false)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        _activeScan = linked;
        _skippedByRoot.Clear();
        _scanning = true; _skipped = 0; _message = "正在扫描磁盘，可继续搜索已有结果"; Notify();
        try
        {
            await Parallel.ForEachAsync(Roots, new ParallelOptions
            { MaxDegreeOfParallelism = 2, CancellationToken = linked.Token }, async (root, token) =>
            {
                await Task.Run(() => ScanRoot(root, token), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            Publish(fastLookup: true);
            _lastScan = DateTime.UtcNow;
            await SaveAsync(linked.Token).ConfigureAwait(false);
            _message = $"索引就绪 · 正在监控文件变化{(_skipped > 0 ? $" · {_skipped:N0} 个目录无法读取" : "")}";
            _log.Write($"Index ready: {_entries.Count} entries; {_skipped} skipped directories.");
        }
        catch (OperationCanceledException)
        {
            _message = "扫描已停止，保留已有索引"; _lastScan = DateTime.UtcNow;
            Interlocked.Exchange(ref _rescanning, 0); Publish(fastLookup: true);
        }
        catch (Exception error)
        { _message = "索引未完成，请重新扫描"; _log.Write("Index rebuild failed", error); }
        finally { _activeScan = null; _scanning = false; _scanGate.Release(); Notify(); }
    }

    public async Task ChangeRootsAsync(IEnumerable<string> roots, CancellationToken cancellation = default)
    {
        string[] normalized = PathRules.NormalizeRoots(roots);
        if (_started) await _cacheReady.Task.WaitAsync(cancellation).ConfigureAwait(false);
        try { _activeScan?.Cancel(); } catch (ObjectDisposedException) { }
        await _scanGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            foreach (FileSystemWatcher watcher in _watchers) watcher.Dispose();
            _watchers.Clear(); Roots = normalized;
            while (_changes.TryDequeue(out _)) Interlocked.Decrement(ref _pendingCount);
            foreach (string path in _entries.Keys)
                if (!Roots.Any(root => PathRules.IsWithin(path, root))) _entries.TryRemove(path, out _);
            Interlocked.Exchange(ref _dirty, 1);
            Publish(); StartWatchers(); Notify();
        }
        finally { _scanGate.Release(); }
        await RebuildAsync(cancellation).ConfigureAwait(false);
    }

    private void LoadCache(CancellationToken token)
    {
        try
        {
            CachedIndex? cached = _cache.Load(token);
            if (cached is null) return;
            foreach (FileEntry entry in cached.Entries)
                if (Roots.Any(root => PathRules.IsWithin(entry.FullPath, root)) && !IsExcluded(entry.FullPath))
                    _entries[entry.FullPath] = entry;
            _lastSaved = cached.SavedUtc;
            _message = "已载入缓存，正在核对磁盘变化";
            Publish(); Notify();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { _log.Write("Cache load failed; rebuilding", error); }
    }

    private void ScanRoot(string root, CancellationToken token)
    {
        if (!Directory.Exists(root))
        { _log.Write($"Root unavailable: {root}"); Interlocked.Increment(ref _skipped); return; }
        string[] excluded = _excluded.Concat(new[]
            { Path.Combine(Path.GetPathRoot(root)!, "$Recycle.Bin"), Path.Combine(Path.GetPathRoot(root)!, "System Volume Information") }).ToArray();
        HashSet<string>? seen = null;
        if (_preferNtfs)
        {
            try
            {
                IReadOnlyList<FileEntry> entries = new NtfsScanner().Scan(root, excluded, Report, token);
                seen = new HashSet<string>(entries.Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
                foreach (FileEntry entry in entries) _entries[entry.FullPath] = entry;
                _mode = "NTFS 快速索引";
                Interlocked.Exchange(ref _dirty, 1);
            }
            catch (Exception error) when (error is Win32Exception or NotSupportedException or IOException)
            { _log.Write($"NTFS fast path unavailable for {root}; using directory scan", error); }
        }
        if (seen is null)
        {
            seen = new DirectoryScanner().Scan(root, excluded, entry =>
            { _entries[entry.FullPath] = entry; Interlocked.Exchange(ref _dirty, 1); }, Report, token);
        }
        foreach (string path in _entries.Keys)
            if (PathRules.IsWithin(path, root) && !seen.Contains(path)) _entries.TryRemove(path, out _);
        Interlocked.Exchange(ref _dirty, 1);
    }

    private void Report(ScanProgress progress)
    {
        _mode = progress.Mode;
        _message = $"{progress.Mode} · {progress.Root} · 已发现 {progress.Indexed:N0} 项";
        // 每个扫描器的跳过数量由结束通知累计，避免并行覆盖。
        _skippedByRoot[progress.Root] = progress.SkippedDirectories;
        _skipped = _skippedByRoot.Values.Sum();
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastReportTick) >= 250)
        { Interlocked.Exchange(ref _lastReportTick, now); Notify(); }
    }
    private readonly ConcurrentDictionary<string, int> _skippedByRoot = new(StringComparer.OrdinalIgnoreCase);

    private void StartWatchers()
    {
        foreach (string root in Roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += (_, eventArgs) => Enqueue(new(eventArgs.FullPath, null, false));
                watcher.Changed += (_, eventArgs) => Enqueue(new(eventArgs.FullPath, null, false));
                watcher.Deleted += (_, eventArgs) => Enqueue(new(eventArgs.FullPath, null, true));
                watcher.Renamed += (_, eventArgs) => Enqueue(new(eventArgs.FullPath, eventArgs.OldFullPath, false));
                watcher.Error += (_, eventArgs) =>
                { _log.Write("File watcher overflow or disconnect", eventArgs.GetException()); Interlocked.Exchange(ref _rescanning, 1); };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { _log.Write($"Unable to monitor {root}", error); Interlocked.Exchange(ref _rescanning, 1); }
        }
    }

    private void Enqueue(FileChange change)
    {
        if (IsExcluded(change.Path)) return;
        if (Volatile.Read(ref _pendingCount) >= MaximumPendingEvents)
        { Interlocked.Exchange(ref _rescanning, 1); return; }
        Interlocked.Increment(ref _pendingCount); _changes.Enqueue(change);
    }

    private bool IsExcluded(string path) => _excluded.Any(root => PathRules.IsWithin(path, root)) ||
        Roots.Any(root => PathRules.IsWithin(path, Path.Combine(Path.GetPathRoot(root)!, "$Recycle.Bin")) ||
            PathRules.IsWithin(path, Path.Combine(Path.GetPathRoot(root)!, "System Volume Information")));

    private async Task MaintainAsync(CancellationToken token)
    {
        DateTime published = DateTime.MinValue;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(MonitorIntervalMilliseconds));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!_scanning) await ApplyChangesAsync(token).ConfigureAwait(false);
                if (Volatile.Read(ref _dirty) != 0 && (DateTime.UtcNow - published).TotalMilliseconds >= SnapshotIntervalMilliseconds)
                { Publish(); published = DateTime.UtcNow; Notify(); }
                if (!_scanning && Volatile.Read(ref _dirtyToSave) != 0 && (DateTime.UtcNow - _lastSaved).TotalSeconds >= SaveIntervalSeconds)
                    await SaveAsync(token).ConfigureAwait(false);
                if (!_scanning && (Interlocked.Exchange(ref _rescanning, 0) != 0 ||
                    (DateTime.UtcNow - _lastScan).TotalHours >= ReconcileIntervalHours))
                    await RebuildAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _log.Write("Index maintenance failed", error); _message = "自动更新中断，请重新扫描"; Notify(); }
    }

    private async Task ApplyChangesAsync(CancellationToken token)
    {
        var changes = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
        var renames = new List<FileChange>();
        while (_changes.TryDequeue(out FileChange? change))
        {
            Interlocked.Decrement(ref _pendingCount);
            if (change.OldPath is not null) renames.Add(change); else changes[change.Path] = change;
        }
        foreach (FileChange rename in renames) RenameTree(rename.OldPath!, rename.Path);
        foreach (FileChange change in changes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (change.Deleted) { RemoveTree(change.Path); continue; }
            try
            {
                FileAttributes attributes = File.GetAttributes(change.Path);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (directory && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    bool known = _entries.ContainsKey(change.Path);
                    _entries[change.Path] = FileEntry.FromPath(change.Path, true);
                    if (!known)
                        await Task.Run(() => new DirectoryScanner().Scan(change.Path, _excluded, entry =>
                        { _entries[entry.FullPath] = entry; }, _ => { }, token), token).ConfigureAwait(false);
                }
                else
                {
                    var info = new FileInfo(change.Path);
                    _entries[change.Path] = FileEntry.FromPath(change.Path, directory, directory ? -1 : info.Length, info.LastWriteTimeUtc.Ticks);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { if (!File.Exists(change.Path) && !Directory.Exists(change.Path)) RemoveTree(change.Path); else _log.Write("File update deferred", error); }
        }
        if (changes.Count > 0 || renames.Count > 0) Interlocked.Exchange(ref _dirty, 1);
    }

    private void RenameTree(string oldPath, string newPath)
    {
        foreach (KeyValuePair<string, FileEntry> pair in _entries)
        {
            if (!PathRules.IsWithin(pair.Key, oldPath)) continue;
            if (!_entries.TryRemove(pair.Key, out FileEntry? entry)) continue;
            string destination = newPath + pair.Key[oldPath.Length..];
            _entries[destination] = FileEntry.FromPath(destination, entry.IsDirectory, entry.Length, entry.ModifiedUtcTicks);
        }
        Enqueue(new(newPath, null, false));
    }

    private void RemoveTree(string path)
    {
        if (_entries.TryRemove(path, out FileEntry? removed) && !removed.IsDirectory) return;
        foreach (string entry in _entries.Keys)
            if (PathRules.IsWithin(entry, path)) _entries.TryRemove(entry, out _);
    }

    private int _dirtyToSave;
    private void Publish(bool fastLookup = false)
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0 && Snapshot.Length == _entries.Count &&
            (!fastLookup || Catalog.HasFastLookup)) return;
        FileEntry[] entries = _entries.Values.ToArray();
        var catalog = new SearchCatalog(entries, buildFastLookup: fastLookup || !_scanning);
        Volatile.Write(ref _catalog, catalog);
        Volatile.Write(ref _snapshot, entries);
        Interlocked.Exchange(ref _dirtyToSave, 1);
    }
    private void Notify() => Changed?.Invoke(State);
    private async Task SaveAsync(CancellationToken token)
    {
        await _cache.SaveAsync(Snapshot, Roots, token).ConfigureAwait(false);
        _lastSaved = DateTime.UtcNow; Interlocked.Exchange(ref _dirtyToSave, 0);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        foreach (FileSystemWatcher watcher in _watchers) watcher.Dispose();
        if (_maintenance is not null)
            try { await _maintenance.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _scanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Publish();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (Snapshot.Length > 0) await SaveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        { _log.Write("Final cache save skipped", error); }
        finally { _scanGate.Release(); _lifetime.Dispose(); }
    }
}
