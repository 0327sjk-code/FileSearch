// 文件用途：打开文件、资源管理器定位和路径复制；定位采用 PIDL，避免命令行转义错误。
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FlashFind.Core;

namespace FlashFind.Desktop.Services;

public interface IFileActions
{
    void Open(FileEntry entry);
    void Locate(FileEntry entry);
    void CopyPath(FileEntry entry);
}

public interface IShellGateway
{
    int SelectItem(IntPtr absoluteItemIdList);
    void OpenPath(string path);
    void CopyText(string text);
}

public sealed class NativeShellGateway : IShellGateway
{
    public int SelectItem(IntPtr absoluteItemIdList) => SHOpenFolderAndSelectItems(absoluteItemIdList, 0, IntPtr.Zero, 0);
    public void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public void CopyText(string text) => Clipboard.SetText(text);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);
}

public sealed class FileActions(IShellGateway gateway) : IFileActions
{
    public void Open(FileEntry entry)
    { ValidateExisting(entry.FullPath); gateway.OpenPath(entry.FullPath); }
    public void CopyPath(FileEntry entry) => gateway.CopyText(entry.FullPath);

    public void Locate(FileEntry entry)
    {
        ValidateExisting(entry.FullPath);
        int initialized = CoInitializeEx(IntPtr.Zero, 2);
        IntPtr item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(entry.FullPath, IntPtr.Zero, out item, 0, out _));
            Marshal.ThrowExceptionForHR(gateway.SelectItem(item));
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static void ValidateExisting(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("文件路径不是绝对路径。");
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("文件已移动或删除，请重新扫描。", path);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint attributes, out uint actual);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
