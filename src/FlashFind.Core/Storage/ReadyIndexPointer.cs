// 文件用途：通过小型指针文件原子切换索引版本，避免 Windows 覆盖仍被旧结果映射的文件。
using System.Text;

namespace FlashFind.Core.Storage;

internal static class ReadyIndexPointer
{
    private const string Magic = "FLASHFIND_READY:1";
    private const int MaximumPointerBytes = 512;
    private const int MinimumCleanupAgeSeconds = 60;

    public static string Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is <= 0 or > MaximumPointerBytes) throw new InvalidDataException("直接查询索引指针损坏。");
        using var reader = new StreamReader(file, Encoding.UTF8);
        if (reader.ReadLine() != Magic) throw new InvalidDataException("直接查询索引指针版本不兼容。");
        string name = reader.ReadLine() ?? "";
        string prefix = System.IO.Path.GetFileName(path) + ".";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".data", StringComparison.Ordinal) ||
            name.Length != prefix.Length + 32 + 5 || !Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _) ||
            name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            throw new InvalidDataException("直接查询索引的数据文件名异常。");
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name);
    }

    public static void Publish(string pointerPath, string dataPath)
    {
        string temporary = pointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] content = Encoding.UTF8.GetBytes(Magic + "\n" + System.IO.Path.GetFileName(dataPath) + "\n");
                file.Write(content); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, pointerPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void CleanOld(string pointerPath, string current)
    {
        try { CleanOldCore(pointerPath, current); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void CleanOldCore(string pointerPath, string current)
    {
        string directory = System.IO.Path.GetDirectoryName(pointerPath)!;
        string prefix = System.IO.Path.GetFileName(pointerPath) + ".";
        foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*.data", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)) continue;
            string name = System.IO.Path.GetFileName(candidate);
            if (name.Length != prefix.Length + 37 || !Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _)) continue;
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate)).TotalSeconds < MinimumCleanupAgeSeconds) continue;
            // 旧结果可能仍在查询这代索引，留到下一次保存或启动再清理。
            try { File.Delete(candidate); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
