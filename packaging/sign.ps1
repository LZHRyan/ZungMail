<#
.SYNOPSIS
    用 Authenticode 给 ZungMail 的程序文件与安装包签名（可选步骤）。

.DESCRIPTION
    未签名的 exe 最容易被 360 安全卫士 / 天擎、火绒、腾讯电脑管家这类启发式引擎
    当成可疑程序直接隔离——表现就是「装完目录里没有 ZungMail.exe」，MSI 还会连带
    报出 1909（快捷方式指向的文件已经没了）。签名是目前唯一能从根上缓解的办法。

    本脚本不强制：没配置证书时只打印提示并返回 0，打包脚本照常产出「未签名包」。
    配置方式（二选一，通过环境变量）：
      - 证书已在个人证书库：ZUNGMAIL_SIGN_THUMBPRINT=<证书指纹>
      - 手上有 PFX 文件：    ZUNGMAIL_SIGN_PFX=D:\certs\zungmail.pfx
                            ZUNGMAIL_SIGN_PFX_PASSWORD=<pfx 口令>
    可选：ZUNGMAIL_SIGN_TIMESTAMP（默认 http://timestamp.digicert.com）

    改名兼容：旧的 MAILCLIENT_SIGN_* 变量仍然生效（已有流水线 / 测试机上配的还是旧名字），
    两套都读、新名优先；两套都配时以 ZUNGMAIL_SIGN_* 为准。

.EXAMPLE
    $env:ZUNGMAIL_SIGN_THUMBPRINT = 'A1B2C3...'
    .\packaging\sign.ps1 -Path .\packaging\obj\publish\ZungMail.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]]$Path,
    # 要求必须签成：配置缺失或签名失败都抛异常（发布正式版时用）
    [switch]$Required
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Find-SignTool {
    $candidates = @(
        (Get-Command 'signtool.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    ) | Where-Object { $_ }

    if ($candidates.Count -gt 0) { return $candidates[0] }

    # Windows SDK 的默认安装位置（x64 主机）
    foreach ($root in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (-not (Test-Path $root)) { continue }
        $found = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    return $null
}

# 读签名配置：新名 ZUNGMAIL_SIGN_* 优先，回退到改名前的 MAILCLIENT_SIGN_*（老流水线不用改）。
function Get-SignEnvironment([string]$Suffix) {
    foreach ($prefix in @('ZUNGMAIL_SIGN_', 'MAILCLIENT_SIGN_')) {
        $value = [Environment]::GetEnvironmentVariable($prefix + $Suffix)
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }
    return $null
}

$thumbprint = Get-SignEnvironment 'THUMBPRINT'
$pfx = Get-SignEnvironment 'PFX'
$pfxPassword = Get-SignEnvironment 'PFX_PASSWORD'
$signCommand = Get-SignEnvironment 'COMMAND'
$timestamp = Get-SignEnvironment 'TIMESTAMP'
if ([string]::IsNullOrWhiteSpace($timestamp)) {
    $timestamp = 'http://timestamp.digicert.com'
}

if ([string]::IsNullOrWhiteSpace($thumbprint) -and [string]::IsNullOrWhiteSpace($pfx) -and
    [string]::IsNullOrWhiteSpace($signCommand)) {
    $message = '未配置代码签名（ZUNGMAIL_SIGN_THUMBPRINT / ZUNGMAIL_SIGN_PFX / ZUNGMAIL_SIGN_COMMAND，兼容旧的 MAILCLIENT_SIGN_*），本次产出未签名：' +
               '360 等安全软件可能把 ZungMail.exe 当可疑程序隔离，安装后目录里会没有主程序。'
    if ($Required) { throw $message }
    Write-Host "    $message" -ForegroundColor Yellow
    return
}

$targets = @($Path | Where-Object { Test-Path -LiteralPath $_ })
if ($targets.Count -eq 0) {
    if ($Required) { throw '签名目标不存在。' }
    Write-Host '    没有需要签名的文件。' -ForegroundColor Yellow
    return
}

# ---------------- 云签通道（开源免费证书基本都是云签，拿不到本地 PFX）----------------
# 例：SignPath / SSL.com eSigner / Certum SimplySign 的命令行工具，{file} 会替换成待签文件。
if (-not [string]::IsNullOrWhiteSpace($signCommand)) {
    foreach ($file in $targets) {
        $line = $signCommand.Replace('{file}', '"' + $file + '"')
        & cmd.exe /c $line | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $message = "云签命令失败（退出码 $LASTEXITCODE）：$file"
            if ($Required) { throw $message }
            Write-Host "    $message" -ForegroundColor Yellow
            continue
        }

        $status = (Get-AuthenticodeSignature -LiteralPath $file).Status
        if ($status -ne 'Valid') {
            $message = "签名后校验未通过（$status）：$file"
            if ($Required) { throw $message }
            Write-Host "    $message" -ForegroundColor Yellow
            continue
        }

        Write-Host ("    已签名（云签）：{0}" -f (Split-Path $file -Leaf)) -ForegroundColor Green
    }

    return
}

$signTool = Find-SignTool
if (-not $signTool) {
    $message = "找不到 signtool.exe。装一个 Windows SDK（含签名工具）或把 signtool 放进 PATH 再打包。"
    if ($Required) { throw $message }
    Write-Host "    $message" -ForegroundColor Yellow
    return
}

foreach ($file in $targets) {
    $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $timestamp)
    if (-not [string]::IsNullOrWhiteSpace($thumbprint)) {
        $arguments += @('/sha1', $thumbprint)
    }
    else {
        $arguments += @('/f', $pfx)
        if (-not [string]::IsNullOrWhiteSpace($pfxPassword)) {
            $arguments += @('/p', $pfxPassword)
        }
    }
    $arguments += $file

    & $signTool @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $message = "签名失败（退出码 $LASTEXITCODE）：$file"
        if ($Required) { throw $message }
        Write-Host "    $message" -ForegroundColor Yellow
        continue
    }

    $status = (Get-AuthenticodeSignature -LiteralPath $file).Status
    if ($status -ne 'Valid') {
        $message = "签名后校验未通过（$status）：$file"
        if ($Required) { throw $message }
        Write-Host "    $message" -ForegroundColor Yellow
        continue
    }

    Write-Host ("    已签名：{0}" -f (Split-Path $file -Leaf)) -ForegroundColor Green
}
