// 文件用途：从程序集内嵌 ICO 读取 32 像素图标，无浏览器和额外桌面框架。
using System.IO;
using System.Windows;

namespace FlashFind.Desktop.Services;

internal sealed class EmbeddedTrayIcon : IDisposable
{
    public IntPtr Handle { get; }
    public EmbeddedTrayIcon()
    {
        using Stream stream = Application.GetResourceStream(new Uri("/文件搜索;component/Assets/FileSearch.ico", UriKind.Relative))!.Stream;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); byte[] ico = buffer.ToArray();
        const int DirectoryOffset = 6, EntrySize = 16, TargetSize = 32;
        int count = BitConverter.ToUInt16(ico, 4), best = DirectoryOffset, distance = int.MaxValue;
        for (int index = 0; index < count; index++)
        {
            int entry = DirectoryOffset + index * EntrySize, size = ico[entry] == 0 ? 256 : ico[entry];
            int delta = Math.Abs(size - TargetSize);
            if (delta < distance) { best = entry; distance = delta; }
        }
        int length = checked((int)BitConverter.ToUInt32(ico, best + 8)), offset = checked((int)BitConverter.ToUInt32(ico, best + 12));
        byte[] pixels = ico.AsSpan(offset, length).ToArray();
        Handle = TrayNative.CreateIconFromResourceEx(pixels, (uint)pixels.Length, true, 0x00030000, TargetSize, TargetSize, 0);
        if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception("托盘图标创建失败。");
    }
    public void Dispose() { if (Handle != IntPtr.Zero) TrayNative.DestroyIcon(Handle); }
}
