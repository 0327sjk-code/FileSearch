// 文件用途：独立后台进程等待旧程序退出，原子替换、健康确认和失败回滚。
using System.Diagnostics;
using System.IO;
using FlashFind.Core.Updating;

namespace FlashFind.Desktop.Updating;

public static class UpdateInstaller
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromMinutes(2), HealthTimeout = TimeSpan.FromMinutes(2);

    public static async Task<int> ApplyAsync(string planPath)
    {
        UpdateInstallPlan? plan = null; string? rollback = null; Process? child = null;
        bool replaced = false, rolledBack = false, parentExited = false;
        try
        {
            plan = UpdateInstallFiles.Read<UpdateInstallPlan>(planPath); UpdateInstallFiles.Validate(plan, planPath);
            string payload = Path.Combine(plan.Stage, UpdateInstallFiles.PayloadName);
            UpdateFileValidation.Verify(payload, plan.Release);
            VerifyOlderTarget(plan);
            await WaitForParentAsync(plan).ConfigureAwait(false);
            parentExited = true;
            VerifyOlderTarget(plan); UpdateFileValidation.Verify(payload, plan.Release);
            string identity = Path.GetFileName(plan.Stage);
            rollback = Path.Combine(Path.GetDirectoryName(plan.Target)!, $".文件搜索.update-{identity}.rollback");
            if (File.Exists(rollback)) throw new IOException("更新事务文件已存在。");
            using var health = new EventWaitHandle(false, EventResetMode.ManualReset, plan.HealthEvent);
            using Process installer = Process.GetCurrentProcess();
            string receiptPath = Path.Combine(plan.Stage, UpdateInstallFiles.ReceiptName);
            UpdateInstallFiles.Save(receiptPath, new UpdateReceipt(plan, installer.Id, installer.StartTime.ToUniversalTime().Ticks));
            File.Replace(payload, plan.Target, rollback); replaced = true;
            child = Launch(plan, receiptPath);
            bool ready = await Task.Run(() => health.WaitOne(plan.Diagnostic ? TimeSpan.FromSeconds(5) : HealthTimeout)).ConfigureAwait(false);
            if (!ready || child.HasExited) throw new IOException("更新后的程序未完成启动。");
            File.Delete(rollback); rollback = null;
            WriteResult(plan, new(true, plan.Release.Version, child.Id, false, null));
            return 0;
        }
        catch (Exception error)
        {
            if (replaced && plan is not null && rollback is not null && File.Exists(rollback))
            {
                try
                {
                    // 这里只结束安装器自己启动且尚未确认健康的子进程。
                    if (child is not null && !child.HasExited) { child.Kill(); await child.WaitForExitAsync().ConfigureAwait(false); }
                    File.Replace(rollback, plan.Target, null); rolledBack = true;
                    using Process restored = Launch(plan, Path.Combine(plan.Stage, UpdateInstallFiles.ReceiptName));
                }
                catch (Exception rollbackError) { error = new AggregateException(error, rollbackError); }
            }
            else if (parentExited && plan is not null && File.Exists(Path.Combine(plan.Stage, UpdateInstallFiles.ReceiptName)))
            {
                try { using Process restored = Launch(plan, Path.Combine(plan.Stage, UpdateInstallFiles.ReceiptName)); }
                catch (Exception restartError) { error = new AggregateException(error, restartError); }
            }
            if (plan is not null) WriteResult(plan, new(false, plan.Release.Version, child?.Id ?? 0, rolledBack, error.Message));
            return 1;
        }
        finally { child?.Dispose(); }
    }

    private static void VerifyOlderTarget(UpdateInstallPlan plan)
    {
        UpdateInstallFiles.EnsureNoReparse(plan.Target);
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(plan.Target);
        Version current = new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        if (info.ProductName != UpdateProduct.ProductName || info.CompanyName != UpdateProduct.CompanyName || current >= plan.Release.Validate())
            throw new InvalidDataException("目标程序身份不正确或不需要升级。");
    }
    private static async Task WaitForParentAsync(UpdateInstallPlan plan)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.HasExited) return;
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartUtcTicks ||
                !string.Equals(parent.MainModule?.FileName, plan.Target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("旧程序进程身份已改变。");
            using var deadline = new CancellationTokenSource(ExitTimeout);
            await parent.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
    }
    private static Process Launch(UpdateInstallPlan plan, string receiptPath) => UpdateInstallFiles.StartHidden(plan.Target,
        plan.Diagnostic ? "--update-probe-restarted" : "--after-update", receiptPath);
    private static void WriteResult(UpdateInstallPlan plan, UpdateInstallResult result)
    {
        try { UpdateInstallFiles.Save(Path.Combine(plan.Root, $"install-result-{Path.GetFileName(plan.Stage)}.json"), result); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
