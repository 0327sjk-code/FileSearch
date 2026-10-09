// 文件用途：安装器与更新诊断的后台启动入口，全部绕过正常窗口和托盘初始化。
using System.IO;
using System.Windows;
using FlashFind.Desktop.Updating;

namespace FlashFind.Desktop;

public partial class App
{
    private bool TryStartUpdateCommand(string[] arguments)
    {
        if (arguments.Length != 2 || arguments[0] is not ("--apply-update" or "--update-probe-parent" or "--update-probe-restarted" or "--verify-updates" or "--check-update-source")) return false;
        _ = RunUpdateCommandAsync(arguments[0], arguments[1]); return true;
    }
    private async Task RunUpdateCommandAsync(string command, string argument)
    {
        try
        {
            int exit = command switch
            {
                "--apply-update" => await UpdateInstaller.ApplyAsync(argument),
                "--update-probe-parent" => await UpdateDiagnostics.ParentProbeAsync(argument),
                "--update-probe-restarted" => await UpdateDiagnostics.RestartedProbeAsync(argument),
                "--check-update-source" => await UpdateDiagnostics.CheckPublicSourceAsync(argument),
                _ => await UpdateDiagnostics.VerifyAsync(argument)
            };
            Shutdown(exit);
        }
        catch (Exception error)
        {
            // 后台入口从不回落到普通窗口；仅在自身诊断/安装目录写失败信息。
            string? directory = command == "--verify-updates" ? argument : Path.GetDirectoryName(Path.GetFullPath(argument));
            if (directory is not null)
                try { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "update-command-failure.txt"), error.ToString()); } catch (IOException) { }
            Shutdown(1);
        }
    }
}
