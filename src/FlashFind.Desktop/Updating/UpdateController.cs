// 文件用途：原生应用每日调度、手动检查、后台下载及空闲后的安装交接。
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Threading;
using FlashFind.Core.Storage;
using FlashFind.Core.Updating;
using FlashFind.Desktop.ViewModels;

namespace FlashFind.Desktop.Updating;

public sealed class UpdateController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1), ReadyInterval = TimeSpan.FromSeconds(1);
    private readonly MainViewModel _model;
    private readonly AppLog _log;
    private readonly Func<UpdateResumeState> _capture;
    private readonly Action _exit;
    private readonly Action<string> _notify;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timer;
    private readonly string _statePath;
    private readonly GitHubUpdateClient _client;
    private DailyUpdateState _state;
    private DateTimeOffset? _retryAfter;
    private long _lastInput = Stopwatch.GetTimestamp();
    private ReleaseManifest? _ready;
    private string? _stage;
    private bool _disposed;
    public UpdateViewModel ViewModel { get; }

    public UpdateController(MainViewModel model, AppLog log, Dispatcher dispatcher, Func<UpdateResumeState> capture,
        Action exit, Action<string> notify)
    {
        _model = model; _log = log; _capture = capture; _exit = exit; _notify = notify;
        _statePath = Path.Combine(AppSettings.DataDirectory, "automatic-update.json"); _state = DailyUpdateState.Load(_statePath);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FileSearch/" + UpdateProduct.DisplayVersion);
        _client = new(_http); ViewModel = new(CheckManually); model.AttachUpdates(ViewModel);
        _timer = new(DispatcherPriority.Background, dispatcher) { Interval = DailyUpdatePolicy.InstallIdleTime };
        _timer.Tick += Tick; InputManager.Current.PreProcessInput += RecordInput; _timer.Start();
    }
    private void RecordInput(object sender, PreProcessInputEventArgs args) => _lastInput = Stopwatch.GetTimestamp();
    public void CheckManually() { if (!_disposed && !ViewModel.Busy) _ = CheckAsync(true); }
    private void Tick(object? sender, EventArgs args)
    {
        if (_disposed) return;
        _timer.Interval = _ready is null ? PollInterval : ReadyInterval;
        if (_ready is not null)
        {
            if (!_model.Searching && !_model.SavingSettings && !_model.SettingsOpen &&
                Stopwatch.GetElapsedTime(_lastInput) >= DailyUpdatePolicy.InstallIdleTime) InstallReady();
        }
        else if (DailyUpdatePolicy.ShouldCheck(DateTime.Now, _state.CompletedDay, _retryAfter, DateTimeOffset.UtcNow, ViewModel.Busy)) _ = CheckAsync(false);
    }
    private async Task CheckAsync(bool manual)
    {
        ViewModel.Checking(); string? stage = null;
        try
        {
            _log.Write("Update check started: " + (manual ? "manual" : "daily"));
            ReleaseManifest release = await _client.CheckAsync(_stop.Token);
            if (_disposed) return;
            if (release.Validate() <= UpdateProduct.CurrentVersion)
            {
                MarkCompleted(); ViewModel.Complete(manual ? $"当前已是最新版本 {UpdateProduct.DisplayVersion}" : "");
                if (manual) _notify("当前已是最新版本 " + UpdateProduct.DisplayVersion);
                _log.Write("Update check complete: current"); return;
            }
            stage = UpdateInstallFiles.CreateStage(UpdateInstallFiles.Root);
            var progress = new Progress<int>(percent => { if (!_disposed) ViewModel.Downloading(release.Version, percent); });
            ViewModel.Downloading(release.Version, 0);
            await _client.DownloadAsync(release, Path.Combine(stage, UpdateInstallFiles.PayloadName), progress, _stop.Token);
            if (_disposed) return;
            _ready = release; _stage = stage; stage = null; _retryAfter = null;
            ViewModel.Ready(release.Version); _timer.Interval = ReadyInterval;
            _notify(release.Version + " 已下载，操作结束后自动重启");
            _log.Write("Update download verified: " + release.Version);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error)
        {
            if (!_disposed)
            {
                _retryAfter = DateTimeOffset.UtcNow + DailyUpdatePolicy.RetryDelay;
                ViewModel.Complete("更新检查失败，15 分钟后重试；可手动检查");
                if (manual) _notify("更新检查失败，请稍后重试");
                _log.Write("Update check/download failed", error);
            }
        }
        finally
        {
            if (stage is not null)
                try { UpdateInstallFiles.CleanStage(UpdateInstallFiles.Root, stage); }
                catch (Exception error) { _log.Write("Update incomplete download cleanup failed", error); }
        }
    }
    private void MarkCompleted()
    {
        _state = new(DailyUpdatePolicy.CompletedAfterSuccess(DateTime.Now, _state.CompletedDay));
        _state.Save(_statePath); _retryAfter = null;
    }
    private void InstallReady()
    {
        if (_ready is null || _stage is null) return;
        DailyUpdateState before = _state;
        try
        {
            MarkCompleted(); UpdateHandoff.Start(_ready, _stage, _capture());
            _log.Write("Update install handoff: " + _ready.Version);
            _stage = null; _ready = null; _timer.Stop(); _exit();
        }
        catch (Exception error)
        {
            _state = before;
            try { _state.Save(_statePath); } catch (Exception saveError) { _log.Write("Update schedule restore failed", saveError); }
            _retryAfter = DateTimeOffset.UtcNow + DailyUpdatePolicy.RetryDelay;
            ViewModel.Complete("更新安装未能启动，请手动检查重试");
            _log.Write("Update install handoff failed", error);
            if (_stage is not null)
                try { UpdateInstallFiles.CleanStage(UpdateInstallFiles.Root, _stage); } catch (Exception cleanupError) { _log.Write("Update handoff cleanup failed", cleanupError); }
            _stage = null; _ready = null;
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _timer.Stop(); _timer.Tick -= Tick; InputManager.Current.PreProcessInput -= RecordInput; _stop.Cancel(); _http.Dispose();
        if (_stage is not null)
            try { UpdateInstallFiles.CleanStage(UpdateInstallFiles.Root, _stage); } catch (Exception error) { _log.Write("Update pending cleanup failed", error); }
    }
}
