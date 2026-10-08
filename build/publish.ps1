# 文件用途：构建独立 Windows x64 发布包，无需目标电脑安装 .NET。
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectDirectory 'src\FlashFind.Desktop\FlashFind.Desktop.csproj'
& dotnet publish $projectFile -c Release -r win-x64 --self-contained true --nologo -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
$program = Join-Path $OutputDirectory '文件搜索.exe'
if (-not (Test-Path -LiteralPath $program)) { throw '发布产物不存在。' }
Get-Item -LiteralPath $program | Select-Object FullName,Length,LastWriteTime
