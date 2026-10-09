// 文件用途：主窗口、托盘和单个原生悬浮窗的生命周期；共用同一份索引与视图模型。
using System.Windows;
using FlashFind.Core.Storage;
using FlashFind.Desktop.Views;
using FlashFind.Desktop.ViewModels;

namespace FlashFind.Desktop.Services;

public interface IDesktopWindowHost
{
    void ShowMain(MainWindow window);
    void HideMain(MainWindow window);
    void ShowQuick(QuickSearchWindow window);
    void HideQuick(QuickSearchWindow window);
    void Exit(MainWindow window);
}

public sealed class NativeDesktopWindowHost : IDesktopWindowHost
{
    public void ShowMain(MainWindow window) { window.WindowState = WindowState.Normal; window.Show(); window.Activate(); window.FocusSearch(); }
    public void HideMain(MainWindow window) => window.Hide();
    public void ShowQuick(QuickSearchWindow window) { window.Show(); window.PlaceNative(window.Placement); }
    public void HideQuick(QuickSearchWindow window) => window.Hide();
    public void Exit(MainWindow window) => window.Close();
}

public sealed class DesktopSession : IDisposable
{
    private readonly MainWindow _main;
    private readonly MainViewModel _model;
    private readonly INotificationArea _tray;
    private readonly IDesktopWindowHost _host;
    private readonly Func<QuickSearchWindow> _createQuick;
    private QuickSearchWindow? _quick;
    private bool _disposed;
    public bool IsInTray { get; private set; }
    public QuickSearchWindow? QuickWindow => _quick;
    public event Action? CheckUpdatesRequested;
    public void NotifyUpdate(string message) { if (IsInTray) _tray.NotifyUpdate(message); }

    public DesktopSession(MainWindow main, MainViewModel model, INotificationArea? tray = null,
        IDesktopWindowHost? host = null, Func<QuickSearchWindow>? createQuick = null)
    {
        _main = main; _model = model; _tray = tray ?? new NativeTrayIcon(); _host = host ?? new NativeDesktopWindowHost();
        _createQuick = createQuick ?? (() => new(model)); _tray.DockEnabled = CurrentDock.Enabled;
        main.StateChanged += StateChanged;
        _tray.RestoreRequested += RestoreMain; _tray.SearchRequested += ShowQuickSearch;
        _tray.ToggleDockRequested += ToggleDock; _tray.ResetDockRequested += ResetDock; _tray.ExitRequested += Exit;
        _tray.CheckUpdatesRequested += CheckUpdates;
    }
    private DockSettings CurrentDock => (_model.Settings.Dock ?? new()).Normalize();
    private QuickSearchWindow GetQuick()
    {
        if (_quick is not null) return _quick;
        _quick = _createQuick(); _quick.RestoreRequested += RestoreMain;
        _quick.PlacementChanged += placement => _model.SaveDockSettings(placement with { Enabled = CurrentDock.Enabled });
        return _quick;
    }
    private void StateChanged(object? sender, EventArgs args) { if (_main.WindowState == WindowState.Minimized) MoveToTray(); }
    public void MoveToTray()
    {
        if (_disposed) return;
        IsInTray = true; _main.SetResultsActive(false); _host.HideMain(_main);
        if (CurrentDock.Enabled) { QuickSearchWindow quick = GetQuick(); quick.Collapse(false); _host.ShowQuick(quick); }
    }
    public void RestoreMain()
    {
        if (_disposed) return;
        if (_quick is not null) { _quick.Collapse(false); _host.HideQuick(_quick); }
        IsInTray = false; _main.SetResultsActive(true); _host.ShowMain(_main);
    }
    public void ShowQuickSearch()
    {
        if (_disposed) return;
        IsInTray = true; _main.SetResultsActive(false); _host.HideMain(_main);
        QuickSearchWindow quick = GetQuick(); _host.ShowQuick(quick); quick.Expand(true);
    }
    public void ToggleDock()
    {
        if (_disposed) return;
        DockSettings updated = CurrentDock with { Enabled = !CurrentDock.Enabled };
        if (!_model.SaveDockSettings(updated)) return;
        _tray.DockEnabled = updated.Enabled;
        if (!updated.Enabled && _quick is not null) { _quick.Collapse(false); _host.HideQuick(_quick); }
        else if (updated.Enabled && IsInTray) { QuickSearchWindow quick = GetQuick(); _host.ShowQuick(quick); }
    }
    public void ResetDock()
    {
        if (_disposed || !_model.SaveDockSettings(new())) return;
        _tray.DockEnabled = true;
        if (_quick is not null) _quick.PlaceNative(CurrentDock);
        if (IsInTray) { QuickSearchWindow quick = GetQuick(); _host.ShowQuick(quick); }
    }
    private void Exit() { if (!_disposed) _host.Exit(_main); }
    private void CheckUpdates() { if (!_disposed) CheckUpdatesRequested?.Invoke(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _main.StateChanged -= StateChanged;
        _tray.RestoreRequested -= RestoreMain; _tray.SearchRequested -= ShowQuickSearch;
        _tray.ToggleDockRequested -= ToggleDock; _tray.ResetDockRequested -= ResetDock; _tray.ExitRequested -= Exit;
        _tray.CheckUpdatesRequested -= CheckUpdates;
        _tray.Dispose(); _quick?.Close();
    }
}
