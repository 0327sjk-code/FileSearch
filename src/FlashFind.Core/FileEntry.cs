// 文件用途：搜索索引条目及类型分类；查询期间不访问磁盘。
namespace FlashFind.Core;

public enum FileCategory { All, Folder, Document, Image, Video, Audio, Archive, Code, Other }

public sealed record FileEntry
{
    // 目录和名称共享；完整路径仅在显示、定位和保存缓存时临时还原。
    public string ParentPath { get; }
    public string Name { get; }
    public string FullPath => Name.Length == 0 ? ParentPath : Path.Combine(ParentPath, Name);
    public string Drive => Path.GetPathRoot(ParentPath) ?? string.Empty;
    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name).ToLowerInvariant();
    public bool IsDirectory { get; }
    public long Length { get; }
    public long ModifiedUtcTicks { get; }
    public FileCategory Category { get; }
    public bool HasUnicodeName { get; }
    public FileIdentity Identity => new(ParentPath, Name);

    internal FileEntry(string parentPath, string name, bool directory, long length, long ticks)
    {
        ParentPath = parentPath; Name = name; IsDirectory = directory; Length = length; ModifiedUtcTicks = ticks;
        Category = FileClassifier.Classify(directory ? string.Empty : Path.GetExtension(name), directory);
        HasUnicodeName = name.AsSpan().IndexOfAnyExceptInRange('\0', '\u007f') >= 0;
    }

    public static FileEntry FromPath(string path, bool directory, long length = -1, long ticks = 0)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(path);
        return new(Path.GetDirectoryName(path) ?? path, name, directory, length, ticks);
    }
}

public static class FileClassifier
{
    private static readonly IReadOnlyDictionary<string, FileCategory> Extensions = CreateExtensions();

    public static FileCategory Classify(string extension, bool directory) => directory
        ? FileCategory.Folder : Extensions.GetValueOrDefault(extension, FileCategory.Other);

    private static Dictionary<string, FileCategory> CreateExtensions()
    {
        var result = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        Add(FileCategory.Document, ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .rtf .csv .odt .epub");
        Add(FileCategory.Image, ".png .jpg .jpeg .gif .webp .bmp .tif .tiff .svg .psd .exr .hdr .tga .dds");
        Add(FileCategory.Video, ".mp4 .mov .avi .mkv .webm .wmv .m4v .mpeg .mpg");
        Add(FileCategory.Audio, ".mp3 .wav .flac .aac .ogg .m4a .aiff .wma");
        Add(FileCategory.Archive, ".zip .7z .rar .tar .gz .bz2 .xz .iso");
        Add(FileCategory.Code, ".cs .cpp .c .h .hpp .py .js .ts .tsx .jsx .json .xml .html .css .yaml .yml .ini .uasset .umap .uproject .uplugin .fbx .blend .obj .usf .ush");
        return result;

        void Add(FileCategory category, string extensions)
        {
            foreach (string extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result[extension] = category;
        }
    }
}
