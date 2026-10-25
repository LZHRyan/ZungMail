<#
.SYNOPSIS
    一键构建 ZungMail 的 Windows 安装包（自包含 / 框架依赖两种形态）。

.DESCRIPTION
    默认流程：
      1. 结束正在运行的 ZungMail（publish 会写入 bin 目录，被占用会失败）
      2. dotnet publish 到 packaging\obj\publish（win-x64，自包含）
      3. 调用本机 NSIS 的 makensis 生成 dist\ZungMail-<版本>-win-x64-<形态>-setup.exe
      4. 额外产出便携版 zip 与 SHA256 校验文件

    依赖：.NET SDK 8（global.json 已锁定 8.0.404）与 NSIS 3（默认路径
    C:\Program Files (x86)\NSIS，也可用 -MakensisPath 指定）。

.EXAMPLE
    .\packaging\build-installer.ps1

.EXAMPLE
    .\packaging\build-installer.ps1 -Version 1.0.1 -FrameworkDependent -NoZip

.EXAMPLE
    .\packaging\build-installer.ps1 -SkipPublish   # 复用上次 publish 结果，只重打安装包
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    # 只打框架依赖版（体积小，但目标机器需装 .NET 8 Desktop Runtime）
    [switch]$FrameworkDependent,

    # 不传则读取 Directory.Build.props 里的 <Version>
    [string]$Version,

    # 跳过 dotnet publish，直接用上次的输出目录重打安装包
    [switch]$SkipPublish,

    # 关闭 ReadyToRun（默认开：冷启动明显更快，但产物约大一倍）
    [switch]$NoReadyToRun,

    # 不生成便携版 zip
    [switch]$NoZip,

    # 写进安装目录 update.json 的默认升级服务地址（域名 / IP / 完整 URL）
    [string]$UpdateServiceUrl,

    # 与 -UpdateServiceUrl 一起写入 update.json 的渠道
    [string]$UpdateChannel = 'stable',

    # 客户端在运行时直接报错退出，而不是自动关闭
    [switch]$KeepClientRunning,

    [string]$MakensisPath,

    # 输出目录，默认 <仓库根>\dist
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# dotnet CLI 兼容层：CLI 在本机可能因系统服务枚举缺陷崩溃（0xC0000005），
# publish 失败时自动退回 dotnet exec MSBuild.dll。
. (Join-Path $PSScriptRoot 'dotnet-cli.ps1')

# 让中文输出在非交互宿主（CI / 终端）里也按 UTF-8 呈现
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Ok([string]$Text) { Write-Host "    $Text" -ForegroundColor Green }

function Fail([string]$Text) {
    Write-Host ""
    Write-Host "打包失败：$Text" -ForegroundColor Red
    exit 1
}

$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'src\ZungMail.App\ZungMail.App.csproj'
$nsisScript = Join-Path $PSScriptRoot 'ZungMail.nsi'
$iconPath = Join-Path $root 'src\ZungMail.App\Assets\Brand\ZungMail.ico'
$publishDir = Join-Path $PSScriptRoot 'obj\publish'

if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $root 'dist' }
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null }

# ---------------- 版本号 ----------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProps = Get-Content (Join-Path $root 'Directory.Build.props')
    $versionNode = $buildProps.SelectSingleNode('/Project/PropertyGroup/Version')
    $Version = if ($versionNode -and -not [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        $versionNode.InnerText.Trim()
    }
    else {
        '1.0.0'
    }
}

# NSIS 的 VIProductVersion 只接受 4 段纯数字
$numericVersion = ($Version -replace '^[vV]', '') -replace '[^0-9.].*$', ''
if ($numericVersion -notmatch '^\d+\.\d+\.\d+$') {
    Fail "版本号「$Version」不是 NSIS 要求的 x.y.z 格式，请用 -Version 指定，例如 -Version 1.0.1"
}

$flavor = if ($FrameworkDependent) { 'fd' } else { 'selfcontained' }
$setupName = "ZungMail-$numericVersion-$Runtime-$flavor-setup.exe"
$setupPath = Join-Path $OutputDir $setupName
$zipPath = Join-Path $OutputDir "ZungMail-$numericVersion-$Runtime-$flavor-portable.zip"

Write-Host "ZungMail 安装包构建" -ForegroundColor White
Write-Ok "仓库根目录 : $root"
Write-Ok "版本号     : $numericVersion"
Write-Ok "目标运行时 : $Runtime（$(if ($FrameworkDependent) { '框架依赖，需装 .NET 8 Desktop Runtime' } else { '自包含，无需预装运行时' })）"
Write-Ok "输出目录   : $OutputDir"

# ---------------- 关闭运行中的客户端 ----------------
Write-Step "检查运行中的 ZungMail"
$running = @(Get-Process -Name ZungMail -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if ($KeepClientRunning) {
        Fail "ZungMail 正在运行（PID $($running[0].Id)），publish 需要覆盖 bin 下的文件。请先关闭客户端，或去掉 -KeepClientRunning 让脚本自动关闭。"
    }

    foreach ($proc in $running) {
        Write-Ok "正在关闭 ZungMail（PID $($proc.Id)）…"
        try { $proc.CloseMainWindow() | Out-Null } catch { }
    }

    $deadline = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name ZungMail -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 300
    }

    $left = @(Get-Process -Name ZungMail -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        Write-Ok "客户端已最小化到托盘，强制结束进程"
        $left | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
    Write-Ok "已关闭运行实例"
}
else {
    Write-Ok "没有运行中的实例"
}

# ---------------- 发布 ----------------
if ($SkipPublish) {
    Write-Step "跳过 dotnet publish"
    if (-not (Test-Path (Join-Path $publishDir 'ZungMail.exe'))) {
        Fail "发布目录不存在或缺少 ZungMail.exe：$publishDir（去掉 -SkipPublish 重新发布）"
    }
    Write-Ok "复用已有输出：$publishDir"
}
else {
    Write-Step "dotnet publish → $publishDir"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    # ReadyToRun：把 IL 预编译成本机代码，冷启动不再 JIT 整套 WPF + DI。
    # 实测装在 C:\Program Files 且过杀软实时扫描的机器上，这一段能省十几秒；代价是产物约翻倍。
    # 体积优先时用 -NoReadyToRun 关掉。
    $readyToRun = if ($NoReadyToRun) { 'false' } else { 'true' }

    try {
        Invoke-DotNetPublishCompat -Project $appProject -PublishDir $publishDir -Configuration $Configuration `
            -Runtime $Runtime -SelfContained ($selfContained -eq 'true') `
            -ExtraProperties @{
                DebugType           = 'None'
                DebugSymbols        = 'false'
                PublishSingleFile   = 'false'
                PublishReadyToRun   = $readyToRun
                PublishTrimmed      = 'false'
            } `
            -ExtraArguments @('--nologo', '--verbosity', 'minimal')
    }
    catch {
        Fail $_.Exception.Message
    }
    if (-not (Test-Path (Join-Path $publishDir 'ZungMail.exe'))) { Fail "发布输出里没有 ZungMail.exe" }

    Get-ChildItem $publishDir -Recurse -Include *.pdb,*.xml -File -ErrorAction SilentlyContinue | Remove-Item -Force

    # 代码签名（可选）：签在打包之前，安装包内解出来的就是已签名的程序，
    # 不然 360 这类启发式引擎容易把未签名的 ZungMail.exe 直接隔离。
    Write-Step "代码签名（可选）"
    $signTargets = @(Join-Path $publishDir 'ZungMail.exe') +
        @(Get-ChildItem $publishDir -Filter 'ZungMail*.dll' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    & (Join-Path $PSScriptRoot 'sign.ps1') -Path $signTargets

    Write-Ok "发布完成"
}

# ---------------- 升级服务默认地址 ----------------
# 客户端只认根地址；接口路径内置。没有 update.json 时用户仍需在设置里填地址。
Write-Step "写入升级服务默认地址（update.json）"
$updateJsonPath = Join-Path $publishDir 'update.json'
$updateJsonTemplate = Join-Path $PSScriptRoot 'update.json'
# 复用上次 publish 时可能残留旧地址，先清掉再按本次参数决定是否写入
if (Test-Path $updateJsonPath) { Remove-Item $updateJsonPath -Force }
if (-not [string]::IsNullOrWhiteSpace($UpdateServiceUrl)) {
    $url = $UpdateServiceUrl.Trim()
    if ($url -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*://') { $url = 'http://' + $url }
    if ($url -notmatch '^https?://') { Fail "升级服务地址只支持 http/https：$UpdateServiceUrl" }

    $payload = [ordered]@{
        updateServiceUrl = $url.TrimEnd('/')
        channel          = $UpdateChannel.Trim().ToLowerInvariant()
    }
    [IO.File]::WriteAllText($updateJsonPath, ($payload | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "已写入：$updateJsonPath（$($payload.updateServiceUrl)）"
}
elseif (Test-Path $updateJsonTemplate) {
    # 模板默认留空；只有真正填了地址才带进安装包，避免客户端读到「有文件但没地址」的半配置状态
    $template = $null
    try { $template = Get-Content $updateJsonTemplate -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $template = $null }
    $templateUrl = if ($template -and $template.PSObject.Properties['updateServiceUrl']) {
        [string]$template.updateServiceUrl
    }
    else { '' }

    if ([string]::IsNullOrWhiteSpace($templateUrl)) {
        Write-Ok "模板未填写地址，安装目录不带 update.json（客户端可在设置里填）"
    }
    else {
        Copy-Item $updateJsonTemplate $updateJsonPath -Force
        Write-Ok "已复制模板：$updateJsonTemplate（$templateUrl）"
    }
}
else {
    Write-Ok "未提供 -UpdateServiceUrl，安装目录不带 update.json"
}

$files = @(Get-ChildItem $publishDir -Recurse -File)
$sizeMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
$sizeKb = [int][math]::Ceiling((($files | Measure-Object -Property Length -Sum).Sum) / 1KB)
Write-Ok ("发布内容   : {0} 个文件，{1} MB" -f $files.Count, $sizeMb)

# ---------------- NSIS ----------------
Write-Step "生成安装包"
if (-not (Test-Path $nsisScript)) { Fail "找不到 NSIS 脚本：$nsisScript" }
if (-not (Test-Path $iconPath)) { Fail "找不到应用图标：$iconPath" }

if ([string]::IsNullOrWhiteSpace($MakensisPath)) {
    $makensis = $null
    $makensisCommand = Get-Command makensis -ErrorAction SilentlyContinue
    if ($makensisCommand) { $makensis = $makensisCommand.Source }
    if (-not $makensis) {
        $candidates = @(
            "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
            "$env:ProgramFiles\NSIS\makensis.exe"
        )
        $makensis = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }
    if (-not $makensis) {
        Fail "找不到 makensis.exe。请安装 NSIS 3（https://nsis.sourceforge.io/），或用 -MakensisPath 指定路径。"
    }
}
else {
    if (-not (Test-Path $MakensisPath)) { Fail "指定的 makensis 不存在：$MakensisPath" }
    $makensis = $MakensisPath
}
Write-Ok "makensis   : $makensis"

if (Test-Path $setupPath) { Remove-Item $setupPath -Force }

$frameworkDefine = if ($FrameworkDependent) { '1' } else { '0' }
& $makensis /V2 `
    "/DVERSION=$numericVersion" `
    "/DSOURCE=$publishDir" `
    "/DOUTPUT=$setupPath" `
    "/DICON=$iconPath" `
    "/DAPPSIZE_KB=$sizeKb" `
    "/DFRAMEWORK_DEPENDENT=$frameworkDefine" `
    $nsisScript

if ($LASTEXITCODE -ne 0) { Fail "makensis 退出码 $LASTEXITCODE" }
if (-not (Test-Path $setupPath)) { Fail "没有生成安装包：$setupPath" }

# 安装包本身也签名：否则下载后双击就被拦，用户连界面都看不到
& (Join-Path $PSScriptRoot 'sign.ps1') -Path @($setupPath)

$setupMb = [math]::Round((Get-Item $setupPath).Length / 1MB, 1)
Write-Ok "安装包     : $setupPath（$setupMb MB）"

# ---------------- 便携版 zip ----------------
if (-not $NoZip) {
    Write-Step "打包便携版 zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Ok "便携版     : $zipPath（$zipMb MB）"
}

# ---------------- 校验和 ----------------
Write-Step "写入 SHA256 校验值"
$hashFile = Join-Path $OutputDir 'SHA256SUMS.txt'
# 覆盖式写入输出目录里所有安装包 / 便携包，混装两种形态时也能一次校验；
# 带上 .msi，否则先跑 build-msi 再跑本脚本会把 MSI 的哈希从校验文件里挤掉
$targets = @(Get-ChildItem $OutputDir -File | Where-Object { $_.Extension -in '.exe', '.msi', '.zip' } | Sort-Object Name)
$lines = foreach ($file in $targets) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
$lines | Set-Content -Path $hashFile -Encoding ASCII
Write-Ok "校验文件   : $hashFile（$($targets.Count) 个文件）"

Write-Host ""
Write-Host "打包完成。" -ForegroundColor Green
Write-Host "  安装包：$setupPath"
if (-not $NoZip) { Write-Host "  便携版：$zipPath" }
Write-Host "  校验值：$hashFile"
