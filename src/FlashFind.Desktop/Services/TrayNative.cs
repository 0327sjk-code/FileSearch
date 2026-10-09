// 文件用途：Win32 托盘图标、菜单和隐藏消息窗口的互操作声明。
using System.Runtime.InteropServices;

namespace FlashFind.Desktop.Services;

internal static class TrayNative
{
    internal const uint Add = 0, Modify = 1, Delete = 2, SetVersion = 4, Flags = 1 | 2 | 4 | 128, InfoFlag = 16;
    internal const int Callback = 0x8001, Restore = 0x8002, ContextMenu = 0x007B, Select = 0x0400, KeySelect = 0x0401, DoubleClick = 0x0203;
    internal const uint ReturnCommand = 0x0100, RightButton = 0x0002, Checked = 0x0008, Separator = 0x0800;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct IconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    internal static extern bool Notify(uint message, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, int message, IntPtr parameter, IntPtr data);
    [DllImport("user32.dll")] internal static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
}
