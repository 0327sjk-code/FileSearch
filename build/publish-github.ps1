# 文件用途：后台构建和验证，上传完整资产到草稿后发布；GitHub 更新地址始终固定。
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$Publish,
    [string]$Notes = '新增每天本地时间上午 11:00 自动检查更新，错过时间在启动后补查；保留主界面与托盘的手动检查。后台下载校验后自动重启，保留硬盘设置及悬浮位置，启动失败自动回滚。'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($outputRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '构建输出必须放到源码仓库之外。' }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
Push-Location $projectRoot
try {
    $pending = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $pending) { throw '发布前请提交完整源码，避免二进制与标签不一致。' }
    [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $version = [string]$props.Project.PropertyGroup.Version
    $productSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\FlashFind.Core\Updating\UpdateProduct.cs'))
    $repository = [regex]::Match($productSource, 'Repository = "([^"]+)"').Groups[1].Value
    $executableName = [regex]::Match($productSource, 'ExecutableName = "([^"]+)"').Groups[1].Value
    $assetName = [regex]::Match($productSource, 'ReleaseAssetName = "([^"]+)"').Groups[1].Value
    if ($version -notmatch '^\d+\.\d+\.\d+$' -or -not $repository -or -not $executableName -or $assetName -notmatch '^[A-Za-z0-9._-]+\.exe$') { throw '产品发布配置无效。' }
    $tag = 'v' + $version
    $package = Join-Path $outputRoot 'package'
    $project = Join-Path $projectRoot 'src\FlashFind.Desktop\FlashFind.Desktop.csproj'
    & dotnet publish $project -c Release -r win-x64 --self-contained true --nologo --artifacts-path (Join-Path $outputRoot 'artifacts') -o $package
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
    $program = Join-Path $package $executableName
    foreach ($diagnostic in @('--verify','--verify-updates')) {
        $diagnosticRoot = Join-Path $outputRoot $diagnostic.TrimStart('-')
        $probe = Start-Process -FilePath $program -ArgumentList @($diagnostic, ('"' + $diagnosticRoot + '"')) -WindowStyle Hidden -PassThru
        $probe.WaitForExit()
        if ($probe.ExitCode -ne 0) { throw "发布包后台自检失败：$diagnostic" }
        $probe.Dispose()
    }
    $oldPackage = Join-Path $outputRoot 'diagnostic-old'
    & dotnet publish $project -c Release -r win-x64 --self-contained true --nologo -p:Version=0.0.0 --artifacts-path (Join-Path $outputRoot 'diagnostic-artifacts') -o $oldPackage
    if ($LASTEXITCODE -ne 0) { throw '旧版本诊断副本构建失败。' }
    & (Join-Path $PSScriptRoot 'test-update.ps1') -NewProgram $program -OldProgram (Join-Path $oldPackage $executableName) -OutputDirectory (Join-Path $outputRoot 'roundtrip')
    if (-not $?) { throw '后台替换与重启回归失败。' }
    $assets = Join-Path $outputRoot 'assets'
    [IO.Directory]::CreateDirectory($assets) | Out-Null
    Copy-Item -LiteralPath $program -Destination (Join-Path $assets $assetName)
    $digest = (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash.ToLowerInvariant()
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $assets 'version.txt'), $version + "`n", $encoding)
    [IO.File]::WriteAllText((Join-Path $assets ($assetName + '.sha256')), $digest + '  ' + $assetName + "`n", $encoding)
    $manifest = [ordered]@{ version = $version; executableUrl = "https://github.com/$repository/releases/download/$tag/$assetName"; sha256 = $digest; size = (Get-Item -LiteralPath $program).Length }
    [IO.File]::WriteAllText((Join-Path $assets 'update.json'), ($manifest | ConvertTo-Json), $encoding)
    $notesPath = Join-Path $outputRoot 'release-body.txt'
    [IO.File]::WriteAllText($notesPath, $Notes, $encoding)
    if ($Publish) {
        $existingTag = & git tag --list $tag
        if ($existingTag) { throw "标签已存在，不覆盖已发布版本：$tag" }
        $repo = & gh repo view $repository --json nameWithOwner,visibility | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $repo.visibility -ne 'PUBLIC') { throw '自动更新需要公开发布仓库。' }
        $branch = & git branch --show-current
        & git tag -a $tag -m "文件搜索 $version"
        if ($LASTEXITCODE -ne 0) { throw '创建标签失败。' }
        & git push origin $branch $tag
        if ($LASTEXITCODE -ne 0) { throw '推送源码与标签失败。' }
        & gh release create $tag --repo $repository --draft --verify-tag --title "文件搜索 $version" --notes-file $notesPath (Join-Path $assets $assetName) (Join-Path $assets 'version.txt') (Join-Path $assets ($assetName + '.sha256')) (Join-Path $assets 'update.json')
        if ($LASTEXITCODE -ne 0) { throw '上传草稿 Release 失败，尚未发布更新。' }
        $releases = & gh api "repos/$repository/releases" | ConvertFrom-Json
        $release = @($releases | Where-Object { $_.tag_name -eq $tag -and $_.draft })
        if ($LASTEXITCODE -ne 0 -or $release.Count -ne 1 -or @($release[0].assets).Count -ne 4) { throw '发布资产不完整，保留草稿。' }
        $uploaded = $release[0].assets | Where-Object name -eq $assetName
        if ($uploaded.size -ne $manifest.size -or $uploaded.digest -ne ('sha256:' + $digest)) { throw 'GitHub 资产大小或哈希不一致，保留草稿。' }
        & gh release edit $tag --repo $repository --draft=false --latest
        if ($LASTEXITCODE -ne 0) { throw '公开发布失败。' }
    }
    [pscustomobject]@{ Version=$version; Repository="https://github.com/$repository"; Release="https://github.com/$repository/releases/tag/$tag"; Program=$program; Sha256=$digest; Published=[bool]$Publish }
} finally { Pop-Location }
