// 文件用途：可配置目录、结果数量、窗口设置和原子配置保存。
using System.Text.Json;

namespace FlashFind.Core.Storage;

public sealed record AppSettings
{
    public string[] Roots { get; init; } = Array.Empty<string>();
    public bool UseAllInternalDrives { get; init; } = true;
    public bool IncludePath { get; init; } = false;
    public bool PreferNtfs { get; init; } = true;
    public double WindowWidth { get; init; } = 1140;
    public double WindowHeight { get; init; } = 780;
    public static string DataDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashFind");
    public static string[] DefaultRoots() => Indexing.DriveDiscovery.InternalDrives()
        .Select(x => x.Root).ToArray();

    public static AppSettings Load(string file)
    {
        if (!File.Exists(file)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { return new(); }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, file, overwrite: true);
    }
}
