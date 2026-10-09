// 文件用途：独立持久化每日检查日期，避免更新重启后重复检查且不改写硬盘设置。
using System.Text.Json;

namespace FlashFind.Core.Updating;

public sealed record DailyUpdateState(DateOnly? CompletedDay)
{
    public static DailyUpdateState Load(string path)
    {
        try { return JsonSerializer.Deserialize<DailyUpdateState>(File.ReadAllText(path)) ?? new(CompletedDay: null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(CompletedDay: null); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, overwrite: true);
    }
}
