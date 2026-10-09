// 文件用途：更新进程交接协议与临时目录约束；只传递恢复状态，不写入搜索日志。
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlashFind.Core.Storage;
using FlashFind.Core.Updating;

namespace FlashFind.Desktop.Updating;

public sealed record UpdateResumeState(bool InTray, string Query, string Category, string? DriveRoot);
public sealed record UpdateInstallPlan(int ParentId, long ParentStartUtcTicks, string Target, string Root, string Stage,
    ReleaseManifest Release, UpdateResumeState Resume, string HealthEvent, bool Diagnostic = false);
public sealed record UpdateReceipt(UpdateInstallPlan Plan, int InstallerId, long InstallerStartUtcTicks);
public sealed record UpdateInstallResult(bool Passed, string Version, int RestartedProcessId, bool RolledBack, string? Error);

public static class UpdateInstallFiles
{
    public static string Root => Path.Combine(AppSettings.DataDirectory, "Updates");
    public const string PlanName = "plan.json", PayloadName = "payload.exe", HelperName = "updater.exe", ReceiptName = "resume.json";

    public static string CreateStage(string root)
    {
        root = Path.GetFullPath(root); EnsureNoReparse(root);
        string stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); return stage;
    }
    public static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
    public static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024) throw new InvalidDataException("更新交接文件过大。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("更新交接文件为空。");
    }
    public static void Validate(UpdateInstallPlan plan, string planPath)
    {
        plan.Release.Validate();
        string root = Path.GetFullPath(plan.Root), stage = Path.GetFullPath(plan.Stage), target = Path.GetFullPath(plan.Target);
        if (!Path.IsPathFullyQualified(plan.Target) || target.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) || plan.Target != target ||
            Path.GetDirectoryName(stage) != root || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _) ||
            Path.GetFullPath(planPath) != Path.Combine(stage, PlanName) ||
            (!plan.Diagnostic && !root.Equals(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) ||
            !Guid.TryParseExact(plan.HealthEvent.Replace("Local\\FlashFind.Update.", "", StringComparison.Ordinal), "N", out _) ||
            plan.ParentId <= 0 || plan.ParentStartUtcTicks <= 0 || plan.Resume.Query.Length > 512)
            throw new InvalidDataException("更新路径或交接状态无效。");
        EnsureNoReparse(root); EnsureNoReparse(stage); EnsureNoReparse(target);
        if (Path.GetDirectoryName(Environment.ProcessPath) != stage) throw new InvalidDataException("安装进程不在更新临时目录中。");
    }
    public static void EnsureNoReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路径不能经过目录链接。");
            current = Path.GetDirectoryName(current);
        }
    }
    public static void CleanStage(string root, string stage)
    {
        root = Path.GetFullPath(root); stage = Path.GetFullPath(stage);
        if (Path.GetDirectoryName(stage) != root || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _))
            throw new IOException("拒绝清理更新目录之外的路径。");
        if (!Directory.Exists(stage)) return;
        EnsureNoReparse(stage);
        if (Directory.EnumerateDirectories(stage).Any()) throw new IOException("更新目录出现未知子目录。");
        foreach (string path in Directory.EnumerateFiles(stage)) { EnsureNoReparse(path); File.Delete(path); }
        Directory.Delete(stage);
    }
    public static Process StartHidden(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("无法启动更新进程。");
    }
}
