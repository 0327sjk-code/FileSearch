// 文件用途：读取鼠标、显示器工作区和当前窗口 DPI；移动本软件的悬浮窗。
using System.Runtime.InteropServices;
using System.Windows;

namespace FlashFind.Desktop.Docking;

public interface IDockEnvironment
{
    Point Cursor { get; }
    DockScreen Resolve(string id);
    DockScreen At(Point pointer);
}

public sealed class NativeDockEnvironment : IDockEnvironment
{
    public Point Cursor { get { DockNative.GetCursorPos(out DockNative.NativePoint point); return new(point.X, point.Y); } }
    public DockScreen Resolve(string id)
    {
        var screens = new List<DockScreen>();
        DockNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref DockNative.NativeRect rect, IntPtr data) =>
        { screens.Add(Read(monitor)); return true; }, IntPtr.Zero);
        return screens.FirstOrDefault(screen => screen.Id == id) ?? screens.First(screen => screen.Primary);
    }
    public DockScreen At(Point pointer) => Read(DockNative.MonitorFromPoint(new() { X = (int)pointer.X, Y = (int)pointer.Y }, 2));
    private static DockScreen Read(IntPtr handle)
    {
        var info = new DockNative.MonitorInfo { Size = Marshal.SizeOf<DockNative.MonitorInfo>(), Device = "" };
        if (!DockNative.GetMonitorInfo(handle, ref info)) throw new System.ComponentModel.Win32Exception();
        return new(info.Device, info.Work.ToRect(), (info.Flags & 1) != 0);
    }
}

internal static class DockNative
{
    internal const int DpiChanged = 0x02E0, DisplayChanged = 0x007E, SettingChanged = 0x001A;
    private const uint NoActivate = 0x0010, NoOwnerZOrder = 0x0200, NoSize = 0x0001;
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    internal delegate bool MonitorCallback(IntPtr monitor, IntPtr context, ref NativeRect rectangle, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr context, IntPtr clip, MonitorCallback callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    internal static double MoveToMonitor(IntPtr window, DockScreen screen)
    {
        SetWindowPos(window, new(-1), (int)screen.WorkArea.Left, (int)screen.WorkArea.Top, 0, 0, NoActivate | NoOwnerZOrder | NoSize);
        return Math.Max(GetDpiForWindow(window), 96) / 96d;
    }
    internal static void Place(IntPtr window, Rect rectangle)
    {
        if (!SetWindowPos(window, new(-1), (int)Math.Round(rectangle.X), (int)Math.Round(rectangle.Y),
            (int)Math.Round(rectangle.Width), (int)Math.Round(rectangle.Height), NoActivate | NoOwnerZOrder))
            throw new System.ComponentModel.Win32Exception();
    }
}
