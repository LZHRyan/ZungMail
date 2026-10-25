<#
.SYNOPSIS
    构建 ZungMail 的 x64 自包含发布并生成 MSI 安装包。

.DESCRIPTION
    流程：dotnet publish -r win-x64 -p:PlatformTarget=x64 → 清理 PDB/XML
    → 生成 WiX 源 → dotnet wix build -arch x64 → dist\ZungMail-<版本>-x64.msi
    → 重算 dist\SHA256SUMS.txt。版本号默认取 Directory.Build.props 的 <Version>。

    依赖：.NET SDK 8；WiX 通过仓库 .config/dotnet-tools.json（wix 5.0.2）恢复。

.EXAMPLE
    .\packaging\build-msi.ps1

.EXAMPLE
    .\packaging\build-msi.ps1 -SkipPublish -Version 1.1.1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version,
    [string]$OutputDir,
    [switch]$SkipPublish,
    [switch]$KeepClientRunning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# dotnet CLI 兼容层：某些机器上 CLI 会因系统服务枚举缺陷崩溃（0xC0000005），
# 这里在 publish / wix 失败时自动退回 dotnet exec MSBuild.dll 或缓存里的 wix.dll。
. (Join-Path $PSScriptRoot 'dotnet-cli.ps1')

$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'src\ZungMail.App\ZungMail.App.csproj'
$publishDir = Join-Path $PSScriptRoot 'obj\msi-publish'
$wixDir = Join-Path $PSScriptRoot 'obj\msi'
$wxsPath = Join-Path $wixDir 'ZungMail.wxs'
$toolsManifest = Join-Path $root '.config\dotnet-tools.json'

if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $root 'dist' }
if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null }
if (-not (Test-Path -LiteralPath $toolsManifest)) {
    throw "缺少 WiX 本地工具清单：$toolsManifest。请先执行 dotnet new tool-manifest 与 dotnet tool install wix --version 5.0.2。"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
    $versionNode = $props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    $Version = [string]$versionNode.Version
}
if ([string]::IsNullOrWhiteSpace($Version) -or $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号必须是三段数字（例如 1.0.0）：$Version"
}

function Stop-RunningClient {
    $running = @(Get-Process -Name 'ZungMail' -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    if ($KeepClientRunning) {
        throw "ZungMail 正在运行（PID $($running[0].Id)）。请先关闭客户端，或去掉 -KeepClientRunning 让脚本自动关闭。"
    }

    Write-Host "正在关闭 ZungMail（PID $($running[0].Id)）…"
    foreach ($process in $running) {
        $process.CloseMainWindow() | Out-Null
    }
    Start-Sleep -Seconds 2
    foreach ($process in $running) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
    Start-Sleep -Milliseconds 500
}

if (-not $SkipPublish) {
    Stop-RunningClient
    Write-Host "==> dotnet publish → $publishDir"
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    Invoke-DotNetPublishCompat -Project $appProject -PublishDir $publishDir -Configuration $Configuration `
        -Runtime 'win-x64' -SelfContained $true
}
else {
    Write-Host "==> 复用发布目录：$publishDir"
}

$clientExe = Join-Path $publishDir 'ZungMail.exe'
$guide = Join-Path $publishDir 'Assets\help\user-guide.html'
if (-not (Test-Path -LiteralPath $clientExe)) { throw "发布目录缺少 ZungMail.exe：$publishDir" }
if (-not (Test-Path -LiteralPath $guide)) { throw "发布目录缺少使用教程：$guide" }

# 只保留运行必需产物；PDB / XML 文档不进 MSI。
Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.Extension -in '.pdb', '.xml' } |
    Remove-Item -Force

# 代码签名（可选）：签在打包之前，MSI 里装出来的就是已签名的程序；
# 未签名的 exe 最容易被 360 / 火绒之类当成可疑文件隔离（表现是装完没有 ZungMail.exe + 1909）。
Write-Host "==> 代码签名（可选）"
$signTargets = @($clientExe) +
    @(Get-ChildItem -LiteralPath $publishDir -Filter 'ZungMail*.dll' -File |
        ForEach-Object { $_.FullName })
& (Join-Path $PSScriptRoot 'sign.ps1') -Path $signTargets

Write-Host "==> 恢复 WiX 本地工具"
Invoke-DotNetToolRestoreCompat
Invoke-WixCompat -WixArguments @('--version')

if (-not (Test-Path -LiteralPath $wixDir)) { New-Item -ItemType Directory -Force -Path $wixDir | Out-Null }
Write-Host "==> 生成 WiX 源"
& (Join-Path $PSScriptRoot 'New-MsiSource.ps1') `
    -PublishDir $publishDir `
    -OutputFile $wxsPath `
    -Version $Version
if ($LASTEXITCODE -ne 0) { throw "生成 WiX 源失败。" }

$msiName = "ZungMail-$Version-x64.msi"
$msiPath = Join-Path $OutputDir $msiName
if (Test-Path -LiteralPath $msiPath) { Remove-Item -LiteralPath $msiPath -Force }

Write-Host "==> 编译 MSI（x64）"
Invoke-WixCompat -WixArguments @('build', $wxsPath, '-arch', 'x64', '-b', $publishDir, '-o', $msiPath)

$pdbPath = [IO.Path]::ChangeExtension($msiPath, '.wixpdb')
if (Test-Path -LiteralPath $pdbPath) { Remove-Item -LiteralPath $pdbPath -Force }

# MSI 本体也签名：企业分发时组策略 / 安全软件常按发布者放行
& (Join-Path $PSScriptRoot 'sign.ps1') -Path @($msiPath)

# 产物校验：ProductVersion 必须是请求的版本，SummaryInformation 的平台必须是 x64。
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($msiPath, 0)
$view = $database.OpenView("SELECT Value FROM Property WHERE Property='ProductVersion'")
$view.Execute()
$record = $view.Fetch()
if ($null -eq $record) { throw "MSI 缺少 ProductVersion 属性。" }
$productVersion = [string]$record.StringData(1)
if ($productVersion -ne $Version) { throw "MSI ProductVersion=$productVersion，期望 $Version。" }

$summary = $database.SummaryInformation(0)
$template = [string]$summary.Property(7)
if ($template -notmatch 'x64') { throw "MSI SummaryInformation 未标记 x64：$template" }

$hashFile = Join-Path $OutputDir 'SHA256SUMS.txt'
$targets = @(Get-ChildItem -LiteralPath $OutputDir -File |
    Where-Object { $_.Extension -in '.exe', '.msi', '.zip' } |
    Sort-Object Name)
$lines = foreach ($target in $targets) {
    $hash = (Get-FileHash -LiteralPath $target.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($target.Name)"
}
[IO.File]::WriteAllText($hashFile, (($lines -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

$sizeMb = [math]::Round((Get-Item -LiteralPath $msiPath).Length / 1MB, 1)
Write-Host ""
Write-Host "MSI 构建完成：$msiPath（$sizeMb MB）" -ForegroundColor Green
Write-Host "版本：$productVersion · 平台：x64 · 校验：$hashFile"
