// 文件用途：更新链后台诊断入口；不创建可见窗口、托盘图标或资源管理器。
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using FlashFind.Core.Storage;
using FlashFind.Core.Updating;
using FlashFind.Desktop.Services;

namespace FlashFind.Desktop.Updating;

public sealed record UpdateProbeControl(string Directory);

public static class UpdateDiagnostics
{
    public static async Task<int> VerifyAsync(string output)
    {
        Directory.CreateDirectory(output);
        IReadOnlyDictionary<string, bool> checks = await UpdateProtocolChecks.RunAsync(output);
        UpdateInstallFiles.Save(Path.Combine(output, "update-verification.json"), new
        { Passed = checks.Values.All(value => value), Checks = checks, Version = UpdateProduct.DisplayVersion,
            ForegroundWindowsOpened = BackgroundWindowAudit.VisibleWindowCount(), ExplorerInvoked = false });
        return checks.Values.All(value => value) && BackgroundWindowAudit.VisibleWindowCount() == 0 ? 0 : 1;
    }
    public static async Task<int> CheckPublicSourceAsync(string outputPath)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FileSearch/" + UpdateProduct.DisplayVersion);
        ReleaseManifest release = await new GitHubUpdateClient(http).CheckAsync();
        UpdateInstallFiles.Save(outputPath, new { Passed = true, Current = UpdateProduct.DisplayVersion, Release = release,
            NewVersionAvailable = release.Validate() > UpdateProduct.CurrentVersion,
            Anonymous = true, ForegroundWindowsOpened = BackgroundWindowAudit.VisibleWindowCount() });
        return BackgroundWindowAudit.VisibleWindowCount() == 0 ? 0 : 1;
    }
    public static async Task<int> ParentProbeAsync(string controlPath)
    {
        UpdateProbeControl control = UpdateInstallFiles.Read<UpdateProbeControl>(controlPath);
        Directory.CreateDirectory(control.Directory);
        using Process self = Process.GetCurrentProcess();
        UpdateInstallFiles.Save(Path.Combine(control.Directory, "parent-ready.json"), new
        { Id = self.Id, StartUtcTicks = self.StartTime.ToUniversalTime().Ticks, Executable = Environment.ProcessPath,
            Version = UpdateProduct.DisplayVersion, ForegroundWindowsOpened = BackgroundWindowAudit.VisibleWindowCount() });
        await WaitForFileAsync(Path.Combine(control.Directory, "exit-parent.signal"), TimeSpan.FromMinutes(3));
        return BackgroundWindowAudit.VisibleWindowCount() == 0 ? 0 : 1;
    }
    public static async Task<int> RestartedProbeAsync(string receiptPath)
    {
        UpdateReceipt receipt = UpdateInstallFiles.Read<UpdateReceipt>(receiptPath);
        UpdateInstallPlan plan = receipt.Plan;
        if (!plan.Diagnostic || !string.Equals(plan.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新验证只能运行独立诊断副本。");
        using Process self = Process.GetCurrentProcess();
        string identity = Path.GetFileName(plan.Stage);
        string settings = Path.Combine(Path.GetDirectoryName(plan.Target)!, "settings.json");
        bool newVersion = UpdateProduct.CurrentVersion == plan.Release.Validate();
        bool failHealth = newVersion && File.Exists(Path.Combine(plan.Stage, "diagnostic-fail-health.signal"));
        UpdateInstallFiles.Save(Path.Combine(plan.Root, "restarted-" + identity + ".json"), new
        { Passed = !failHealth, Id = self.Id, Version = UpdateProduct.DisplayVersion, Resume = plan.Resume,
            SettingsSha256 = File.Exists(settings) ? HashFile(settings) : null,
            ForegroundWindowsOpened = BackgroundWindowAudit.VisibleWindowCount(), ExplorerInvoked = false });
        if (failHealth) return 1;
        if (newVersion) { using EventWaitHandle health = EventWaitHandle.OpenExisting(plan.HealthEvent); health.Set(); }
        await WaitForFileAsync(Path.Combine(plan.Root, "finish-" + identity + ".signal"), TimeSpan.FromMinutes(3));
        try
        {
            using Process worker = Process.GetProcessById(receipt.InstallerId);
            if (worker.StartTime.ToUniversalTime().Ticks == receipt.InstallerStartUtcTicks)
                await worker.WaitForExitAsync();
        }
        catch (ArgumentException) { }
        UpdateInstallFiles.CleanStage(plan.Root, plan.Stage);
        return BackgroundWindowAudit.VisibleWindowCount() == 0 ? 0 : 1;
    }
    public static string HashFile(string path) { using FileStream stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path)) { if (watch.Elapsed > timeout) throw new TimeoutException("后台诊断等待超时。"); await Task.Delay(25); }
    }
}
