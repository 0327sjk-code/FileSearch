# 文件用途：只运行独立后台诊断副本，验证真实 EXE 替换、自动重启及状态保留。
param(
    [Parameter(Mandatory = $true)][string]$NewProgram,
    [Parameter(Mandatory = $true)][string]$OldProgram,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
$productSource = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'src\FlashFind.Core\Updating\UpdateProduct.cs'))
$repository = [regex]::Match($productSource, 'Repository = "([^"]+)"').Groups[1].Value
$assetName = [regex]::Match($productSource, 'ReleaseAssetName = "([^"]+)"').Groups[1].Value
if (-not $repository -or -not $assetName) { throw '发布仓库配置不存在。' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$newVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($NewProgram)
$oldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($OldProgram)
if ($newVersion.FileVersion -le $oldVersion.FileVersion) { throw '诊断旧程序版本必须低于新程序。' }
$releaseVersion = '{0}.{1}.{2}' -f $newVersion.FileMajorPart,$newVersion.FileMinorPart,$newVersion.FileBuildPart
$newHash = (Get-FileHash -LiteralPath $NewProgram -Algorithm SHA256).Hash
$reports = [Collections.Generic.List[object]]::new()

function Wait-DiagnosticFile([string]$Path) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($watch.Elapsed.TotalSeconds -gt 60) { throw "后台诊断文件等待超时：$Path" }
        Start-Sleep -Milliseconds 25
    }
}
function Save-DiagnosticJson([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
function Assert-Diagnostic([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "更新回归失败：$Name" }
}

foreach ($case in @(@{ InTray = $false; FailHealth = $false }, @{ InTray = $true; FailHealth = $false }, @{ InTray = $true; FailHealth = $true },
                   @{ InTray = $false; FailHealth = $false; FileName = $assetName }, @{ InTray = $true; FailHealth = $false; FileName = $assetName })) {
    $inTray = $case.InTray
    $caseRoot = Join-Path $root ([Guid]::NewGuid().ToString('N'))
    $installDirectory = Join-Path $caseRoot '应用 中文 空格'
    $updatesRoot = Join-Path $caseRoot 'Updates'
    $identity = [Guid]::NewGuid().ToString('N')
    $stage = Join-Path $updatesRoot $identity
    [IO.Directory]::CreateDirectory($installDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $target = Join-Path $installDirectory $(if ($case.FileName) { $case.FileName } else { '文件搜索.exe' })
    $settingsPath = Join-Path $installDirectory 'settings.json'
    Copy-Item -LiteralPath $OldProgram -Destination $target
    Save-DiagnosticJson $settingsPath @{ UseAllInternalDrives = $false; Roots = @('C:\','D:\'); Dock = @{ Edge = 'left'; Position = 0.67; Enabled = $true } }
    $settingsHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $NewProgram -Destination (Join-Path $stage 'payload.exe')
    $helperPath = Join-Path $stage 'updater.exe'
    Copy-Item -LiteralPath $NewProgram -Destination $helperPath
    if ($case.FailHealth) { [IO.File]::WriteAllText((Join-Path $stage 'diagnostic-fail-health.signal'), '') }
    $controlPath = Join-Path $caseRoot 'control.json'
    Save-DiagnosticJson $controlPath @{ Directory = $caseRoot }
    $parent = $null; $helper = $null; $child = $null
    try {
        $parent = Start-Process -FilePath $target -ArgumentList @('--update-probe-parent', ('"' + $controlPath + '"')) -WindowStyle Hidden -PassThru
        $parentReady = Join-Path $caseRoot 'parent-ready.json'
        Wait-DiagnosticFile $parentReady
        $parentInfo = Get-Content -LiteralPath $parentReady -Raw | ConvertFrom-Json
        Assert-Diagnostic ($parentInfo.ForegroundWindowsOpened -eq 0) '旧程序没有显示窗口'
        $planPath = Join-Path $stage 'plan.json'
        $plan = @{
            ParentId = $parentInfo.Id; ParentStartUtcTicks = $parentInfo.StartUtcTicks; Target = $target
            Root = $updatesRoot; Stage = $stage; Diagnostic = $true
            HealthEvent = 'Local\FlashFind.Update.' + [Guid]::NewGuid().ToString('N')
            Release = @{ Version = $releaseVersion; ExecutableUrl = "https://github.com/$repository/releases/download/v$releaseVersion/$assetName"; Sha256 = $newHash; Size = (Get-Item -LiteralPath $NewProgram).Length }
            Resume = @{ InTray = $inTray; Query = '中文 搜索 [1] & 文件'; Category = 'Image'; DriveRoot = 'D:\' }
        }
        Save-DiagnosticJson $planPath $plan
        $helper = Start-Process -FilePath $helperPath -ArgumentList @('--apply-update', ('"' + $planPath + '"')) -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 300
        Assert-Diagnostic (-not $parent.HasExited -and (Get-FileHash -LiteralPath $target).Hash -ne $newHash) '旧进程运行期间不替换程序'
        [IO.File]::WriteAllText((Join-Path $caseRoot 'exit-parent.signal'), '')
        if (-not $parent.WaitForExit(60000)) { throw '旧诊断进程没有正常退出。' }
        if (-not $helper.WaitForExit(60000)) { throw '安装诊断进程未完成。' }
        Assert-Diagnostic ($helper.ExitCode -eq $(if ($case.FailHealth) { 1 } else { 0 })) '后台安装器返回正确结果'
        $resultPath = Join-Path $updatesRoot "install-result-$identity.json"
        Wait-DiagnosticFile $resultPath
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $restartPath = Join-Path $updatesRoot "restarted-$identity.json"
        Wait-DiagnosticFile $restartPath
        $restarted = Get-Content -LiteralPath $restartPath -Raw | ConvertFrom-Json
        if ($case.FailHealth) {
            $restartWait = [Diagnostics.Stopwatch]::StartNew()
            while ($restarted.Version -eq $releaseVersion) {
                if ($restartWait.Elapsed.TotalSeconds -gt 60) { throw '回滚后的旧程序未启动。' }
                Start-Sleep -Milliseconds 25
                $restarted = Get-Content -LiteralPath $restartPath -Raw | ConvertFrom-Json
            }
        }
        $child = [Diagnostics.Process]::GetProcessById($restarted.Id)
        $null = $child.Handle
        $expectedVersion = $(if ($case.FailHealth) { $parentInfo.Version } else { $releaseVersion })
        $expectedHash = $(if ($case.FailHealth) { (Get-FileHash -LiteralPath $OldProgram).Hash } else { $newHash })
        Assert-Diagnostic ($result.Passed -eq (-not $case.FailHealth) -and $result.RolledBack -eq $case.FailHealth) '安装或失败回滚符合预期'
        Assert-Diagnostic ($restarted.Id -ne $parentInfo.Id -and $restarted.Version -eq $expectedVersion) '自动启动了正确版本进程'
        Assert-Diagnostic ((Get-FileHash -LiteralPath $target).Hash -eq $expectedHash) '安装或回滚文件SHA256一致'
        Assert-Diagnostic ($restarted.Resume.InTray -eq $inTray -and $restarted.Resume.Query -eq $plan.Resume.Query -and $restarted.Resume.Category -eq 'Image' -and $restarted.Resume.DriveRoot -eq 'D:\') '查询和主窗口或托盘状态保留'
        Assert-Diagnostic ($restarted.SettingsSha256 -eq $settingsHash -and (Get-FileHash -LiteralPath $settingsPath).Hash -eq $settingsHash) '硬盘范围和浮栏位置设置未改写'
        Assert-Diagnostic ($restarted.ForegroundWindowsOpened -eq 0 -and -not $restarted.ExplorerInvoked) '新版后台验证未操作桌面'
        Assert-Diagnostic (@(Get-ChildItem -LiteralPath $installDirectory -Force -Filter '*.rollback').Count -eq 0) '成功后无遗留回滚文件'
        [IO.File]::WriteAllText((Join-Path $updatesRoot "finish-$identity.signal"), '')
        if (-not $child.WaitForExit(60000)) { throw '新版诊断进程未退出。' }
        Assert-Diagnostic ($child.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $stage)) '临时安装器清理且诊断进程正常退出'
        $reports.Add([pscustomobject]@{ Passed = $true; InTray = $inTray; FileName = [IO.Path]::GetFileName($target); HealthFailure = $case.FailHealth; RolledBack = $result.RolledBack; Before = $parentInfo.Version; After = $restarted.Version; SettingsPreserved = $true; ResumePreserved = $true; ForegroundWindowsOpened = 0; ExplorerInvoked = $false; StageCleaned = $true; CaseDirectory = $caseRoot })
    } finally {
        # 只清理当前脚本明确创建的诊断子进程，不接触用户正在运行的软件。
        foreach ($ownedProcess in @($parent,$helper,$child)) {
            if ($null -ne $ownedProcess) {
                if (-not $ownedProcess.HasExited) { $ownedProcess.Kill(); $ownedProcess.WaitForExit() }
                $ownedProcess.Dispose()
            }
        }
    }
}
Save-DiagnosticJson (Join-Path $root 'update-roundtrip.json') @{ Passed = $true; Cases = $reports.ToArray() }
$reports.ToArray() | Format-Table Passed,InTray,Before,After,SettingsPreserved,StageCleaned
