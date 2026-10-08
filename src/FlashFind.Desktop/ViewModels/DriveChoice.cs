// 文件用途：硬盘设置选项；列表仅由本机内置设备生成。
using FlashFind.Core.Indexing;

namespace FlashFind.Desktop.ViewModels;

public sealed class DriveChoice(IndexedDrive drive, bool selected) : ObservableObject
{
    private bool _selected = selected;
    public string Root => drive.Root;
    public string Title => $"{drive.Root.TrimEnd('\\')}  {drive.Label}";
    public string Description => $"内置硬盘 · {drive.FileSystem}";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}
public sealed record CategoryChoice(string Title, FlashFind.Core.FileCategory Category);
public sealed record DriveFilter(string Title, string? Root);
