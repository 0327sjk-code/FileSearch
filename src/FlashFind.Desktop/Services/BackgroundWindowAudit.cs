// 文件用途：只读核查当前验证进程是否创建了可见窗口，不控制任何桌面窗口。
using System.Runtime.InteropServices;

namespace FlashFind.Desktop.Services;

public static class BackgroundWindowAudit
{
    public static int VisibleWindowCount()
    {
        int count = 0;
        uint process = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == process && IsWindowVisible(window)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
}
