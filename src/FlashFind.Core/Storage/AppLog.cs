// 文件用途：轮转诊断日志；记录异常、索引阶段，不记录用户搜索词。
namespace FlashFind.Core.Storage;

public sealed class AppLog(string directory)
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    public void Write(string message, Exception? error = null)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "flashfind.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{(error is null ? "" : " | " + error)}{Environment.NewLine}");
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
