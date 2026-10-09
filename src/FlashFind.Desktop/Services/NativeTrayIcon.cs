// 文件用途：系统托盘恢复、搜索、悬浮开关及退出，处理 Explorer 重启后的图标恢复。
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FlashFind.Desktop.Docking;

namespace FlashFind.Desktop.Services;

public interface INotificationArea : IDisposable
{
    event Action? RestoreRequested;
    event Action? SearchRequested;
    event Action? ToggleDockRequested;
    event Action? ResetDockRequested;
    event Action? ExitRequested;
    event Action? CheckUpdatesRequested;
    bool DockEnabled { get; set; }
    void NotifyUpdate(string message);
}

public sealed class NativeTrayIcon : INotificationArea
{
    private const string HostTitle = "FlashFind.NotificationArea.Host";
    private const uint RestoreId = 1, SearchId = 2, ToggleId = 3, ResetId = 4, ExitId = 5, UpdateId = 6;
    private readonly HwndSource _source;
    private readonly EmbeddedTrayIcon _icon;
    private readonly uint _taskbarCreated = TrayNative.RegisterWindowMessage("TaskbarCreated");
    private TrayNative.IconData _data;
    private bool _disposed;
    public event Action? RestoreRequested, SearchRequested, ToggleDockRequested, ResetDockRequested, ExitRequested, CheckUpdatesRequested;
    public bool DockEnabled { get; set; } = true;

    public NativeTrayIcon()
    {
        _icon = new();
        _source = new(new HwndSourceParameters(HostTitle) { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 });
        _source.AddHook(Messages);
        _data = new() { Size = (uint)Marshal.SizeOf<TrayNative.IconData>(), Window = _source.Handle, Id = 1,
            Flags = TrayNative.Flags, CallbackMessage = TrayNative.Callback, Icon = _icon.Handle,
            Tip = "文件搜索 · 点击打开，右键菜单", Info = "", Title = "", Version = 4 };
        if (!Install()) { Dispose(); throw new System.ComponentModel.Win32Exception("系统托盘初始化失败。"); }
    }
    private bool Install()
    {
        if (!TrayNative.Notify(TrayNative.Add, ref _data)) return false;
        TrayNative.Notify(TrayNative.SetVersion, ref _data); return true;
    }
    public static void RestoreExisting()
    {
        IntPtr host = TrayNative.FindWindow(null, HostTitle);
        if (host != IntPtr.Zero) TrayNative.PostMessage(host, TrayNative.Restore, IntPtr.Zero, IntPtr.Zero);
    }
    public void NotifyUpdate(string message)
    {
        if (_disposed) return;
        TrayNative.IconData notification = _data;
        notification.Flags = TrayNative.InfoFlag; notification.Info = message; notification.Title = "文件搜索"; notification.InfoFlags = 1;
        TrayNative.Notify(TrayNative.Modify, ref notification);
    }
    private IntPtr Messages(IntPtr window, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if ((uint)message == _taskbarCreated) { Install(); return IntPtr.Zero; }
        if (message == TrayNative.Restore) { handled = true; RestoreRequested?.Invoke(); return IntPtr.Zero; }
        if (message != TrayNative.Callback) return IntPtr.Zero;
        handled = true;
        int notification = (int)(data.ToInt64() & 0xffff);
        if (notification is TrayNative.Select or TrayNative.KeySelect or TrayNative.DoubleClick) RestoreRequested?.Invoke();
        else if (notification == TrayNative.ContextMenu) ShowMenu();
        return IntPtr.Zero;
    }
    private void ShowMenu()
    {
        IntPtr menu = TrayNative.CreatePopupMenu();
        try
        {
            TrayNative.AppendMenu(menu, 0, new(RestoreId), "打开文件搜索");
            TrayNative.AppendMenu(menu, 0, new(SearchId), "便捷搜索");
            TrayNative.AppendMenu(menu, DockEnabled ? TrayNative.Checked : 0, new(ToggleId), "显示悬浮搜索");
            TrayNative.AppendMenu(menu, 0, new(ResetId), "重置到主屏右侧");
            TrayNative.AppendMenu(menu, 0, new(UpdateId), "检查更新");
            TrayNative.AppendMenu(menu, TrayNative.Separator, UIntPtr.Zero, "");
            TrayNative.AppendMenu(menu, 0, new(ExitId), "退出文件搜索");
            DockNative.GetCursorPos(out DockNative.NativePoint cursor);
            TrayNative.SetForegroundWindow(_source.Handle);
            uint selected = TrayNative.TrackPopupMenuEx(menu, TrayNative.ReturnCommand | TrayNative.RightButton, cursor.X, cursor.Y, _source.Handle, IntPtr.Zero);
            TrayNative.PostMessage(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            switch (selected)
            {
                case RestoreId: RestoreRequested?.Invoke(); break;
                case SearchId: SearchRequested?.Invoke(); break;
                case ToggleId: ToggleDockRequested?.Invoke(); break;
                case ResetId: ResetDockRequested?.Invoke(); break;
                case ExitId: ExitRequested?.Invoke(); break;
                case UpdateId: CheckUpdatesRequested?.Invoke(); break;
            }
        }
        finally { TrayNative.DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        TrayNative.Notify(TrayNative.Delete, ref _data); _source.RemoveHook(Messages); _source.Dispose(); _icon.Dispose();
    }
}
