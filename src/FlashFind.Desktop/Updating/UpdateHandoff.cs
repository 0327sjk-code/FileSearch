// 文件用途：准备安装器、读取启动恢复状态、健康确认及退出后的安全清理。
using System.Diagnostics;
using System.IO;
using FlashFind.Core.Storage;
using FlashFind.Core.Updating;

namespace FlashFind.Desktop.Updating;

public static class UpdateHandoff
{
    public static void Start(ReleaseManifest release, string stage, UpdateResumeState resume)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("无法取得当前程序路径。");
        using Process current = Process.GetCurrentProcess();
        var plan = new UpdateInstallPlan(current.Id, current.StartTime.ToUniversalTime().Ticks, executable,
            UpdateInstallFiles.Root, stage, release, resume, "Local\\FlashFind.Update." + Guid.NewGuid().ToString("N"));
        string helper = Path.Combine(stage, UpdateInstallFiles.HelperName), planPath = Path.Combine(stage, UpdateInstallFiles.PlanName);
        File.Copy(executable, helper);
        UpdateInstallFiles.Save(planPath, plan);
        using Process worker = UpdateInstallFiles.StartHidden(helper, "--apply-update", planPath);
    }

    public static UpdateReceipt? ReadReceipt(string[] arguments, AppLog log)
    {
        if (arguments.Length != 2 || arguments[0] != "--after-update") return null;
        try
        {
            UpdateReceipt receipt = UpdateInstallFiles.Read<UpdateReceipt>(arguments[1]);
            UpdateInstallPlan plan = receipt.Plan;
            if (plan.Diagnostic || !string.Equals(plan.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(plan.Root), Path.GetFullPath(UpdateInstallFiles.Root), StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(Path.GetFullPath(plan.Stage)) != Path.GetFullPath(plan.Root) ||
                Path.GetFullPath(arguments[1]) != Path.Combine(Path.GetFullPath(plan.Stage), UpdateInstallFiles.ReceiptName) ||
                plan.Resume.Query.Length > 512) throw new InvalidDataException("启动恢复文件无效。");
            UpdateInstallFiles.EnsureNoReparse(plan.Stage); return receipt;
        }
        catch (Exception error) { log.Write("Update resume rejected", error); return null; }
    }
    public static void Acknowledge(UpdateReceipt receipt, AppLog log)
    {
        try { using EventWaitHandle ready = EventWaitHandle.OpenExisting(receipt.Plan.HealthEvent); ready.Set(); }
        catch (Exception error) { log.Write("Update health acknowledgment failed", error); }
        log.Write("Update restart ready: " + UpdateProduct.DisplayVersion);
        _ = CleanAfterInstallerAsync(receipt, log);
    }
    private static async Task CleanAfterInstallerAsync(UpdateReceipt receipt, AppLog log)
    {
        try
        {
            try
            {
                using Process worker = Process.GetProcessById(receipt.InstallerId);
                if (worker.StartTime.ToUniversalTime().Ticks == receipt.InstallerStartUtcTicks)
                { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            }
            catch (ArgumentException) { }
            UpdateInstallFiles.CleanStage(receipt.Plan.Root, receipt.Plan.Stage);
        }
        catch (Exception error) { log.Write("Update temporary cleanup deferred", error); }
    }
}
