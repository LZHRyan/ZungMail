<#
.SYNOPSIS
    根据 dotnet publish 输出生成 WiX v4/v5 的 .wxs 源文件。

.DESCRIPTION
    递归遍历发布目录，生成：
      - Program Files\ZungMail 下的目录树与文件组件（Component GUID 由相对路径稳定派生）
      - 开始菜单快捷方式、ARP 图标与 ProductVersion
    输出文件交给 `dotnet wix build -arch x64` 编译成 MSI。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PublishDir,
    [Parameter(Mandatory)] [string]$OutputFile,
    [string]$Version = '1.0.0',
    [string]$ProductName = 'ZungMail',
    [string]$Manufacturer = 'Leung',
    [string]$UpgradeCode = '{D2A9F5D4-6D7B-4F7A-9C2E-71B5E3A8C6F1}'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $PublishDir)) {
    throw "发布目录不存在：$PublishDir"
}
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "MSI 版本号必须是 三段数字（例如 1.0.0）：$Version"
}

function ConvertTo-XmlText([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

function New-StableGuid([string]$key) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($key))
        # RFC 4122 version-5 风格，仅用于保证同一相对路径跨构建稳定。
        $bytes[6] = ($bytes[6] -band 0x0F) -bor 0x50
        $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
        return (New-Object Guid (,$bytes)).ToString().ToUpperInvariant()
    }
    finally {
        $md5.Dispose()
    }
}

function New-Node([string]$id, [string]$name) {
    return [pscustomobject]@{
        Id       = $id
        Name     = $name
        Files    = [System.Collections.Generic.List[object]]::new()
        Children = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    }
}

$root = New-Node 'INSTALLFOLDER' ''
$nextDirectory = 0
$nextFile = 0
$nextComponent = 0
$allFiles = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File |
    Where-Object { $_.Extension -notin '.pdb', '.xml' } |
    Sort-Object FullName)

foreach ($file in $allFiles) {
    $relative = $file.FullName.Substring($PublishDir.Length).TrimStart([char[]]@('\', '/'))
    $parts = @($relative -split '[\\/]')
    if ($parts.Count -eq 0 -or $parts[-1] -eq '') { continue }

    $node = $root
    for ($i = 0; $i -lt ($parts.Count - 1); $i++) {
        $part = $parts[$i]
        if (-not $node.Children.ContainsKey($part)) {
            $nextDirectory++
            $child = New-Node ("d{0:D6}" -f $nextDirectory) $part
            $node.Children[$part] = $child
        }
        $node = $node.Children[$part]
    }

    $nextFile++
    $nextComponent++
    $node.Files.Add([pscustomobject]@{
        Id        = ("f{0:D6}" -f $nextFile)
        Component = ("c{0:D6}" -f $nextComponent)
        Name      = $parts[-1]
        Relative  = $relative
        Guid      = New-StableGuid ("ZungMail/" + $relative.ToLowerInvariant())
    }) | Out-Null
}

if ($allFiles.Count -eq 0) {
    throw "发布目录没有可打包的文件：$PublishDir"
}

$sb = [System.Text.StringBuilder]::new()
function Add-Line([string]$line = '') {
    [void]$sb.AppendLine($line)
}

function Write-Node([object]$node, [int]$indent) {
    $pad = '  ' * $indent
    foreach ($file in ($node.Files | Sort-Object Name)) {
        Add-Line "$pad<Component Id=""$($file.Component)"" Guid=""$($file.Guid)"">"
        Add-Line "$pad  <File Id=""$($file.Id)"" Source=""$(ConvertTo-XmlText $file.Relative)"" KeyPath=""yes"" />"
        Add-Line "$pad</Component>"
    }

    foreach ($name in ($node.Children.Keys | Sort-Object)) {
        $child = $node.Children[$name]
        Add-Line "$pad<Directory Id=""$($child.Id)"" Name=""$(ConvertTo-XmlText $child.Name)"">"
        Write-Node $child ($indent + 1)
        Add-Line "$pad</Directory>"
    }
}

Add-Line '<?xml version="1.0" encoding="utf-8"?>'
Add-Line '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
Add-Line "  <Package Name=""$(ConvertTo-XmlText $ProductName)"" Manufacturer=""$(ConvertTo-XmlText $Manufacturer)"" Version=""$Version"" UpgradeCode=""$UpgradeCode"" Scope=""perMachine"" InstallerVersion=""500"">"
Add-Line '    <MajorUpgrade AllowSameVersionUpgrades="yes" DowngradeErrorMessage="已安装更高版本的 ZungMail。" />'
Add-Line '    <MediaTemplate EmbedCab="yes" />'
Add-Line '    <!-- 默认写安装日志（%TEMP%\MSI*.log）：别的机器装不上时不用重装也能拿到原因 -->'
Add-Line '    <Property Id="MsiLogging" Value="voicewarmupx" />'
Add-Line '    <!-- 只做 64 位检查，不做操作系统版本检查。'
Add-Line '         原因：msiexec 的兼容模式会把 VersionNT / VersionNT64 / ServicePackLevel / WindowsBuild'
Add-Line '         全部按 603→601→600→502→501、WindowsBuild=9600 的样子模拟，'
Add-Line '         「VersionNT >= 1000」「WindowsBuild >= 14393」都必然为假 —— 装 Win10 上也会弹'
Add-Line '         「需要 Windows 10 1607 或更高版本」然后失败（正是「装完没有 ZungMail.exe」的根因）。'
Add-Line '         运行环境的真实校验交给客户端自己（WebView2 / .NET 8 起不来会明确报错），NSIS 安装包同理。 -->'
Add-Line '    <Launch Condition="VersionNT64" Message="ZungMail 只能安装在 64 位 Windows 上。" />'
Add-Line '    <Icon Id="ZungMailIcon" SourceFile="ZungMail.exe" />'
Add-Line '    <Property Id="ARPPRODUCTICON" Value="ZungMailIcon" />'
Add-Line '    <Property Id="ARPNOMODIFY" Value="1" />'
Add-Line '    <!-- 安装完成后自动启动客户端：升级时做到「点一下 → 装好 → 自动回来」。'
Add-Line '         静默安装（/qn，例如域内批量部署）不启动；运维需要彻底关闭时传 LAUNCHAPP=0 -->'
Add-Line '    <Property Id="LAUNCHAPP" Value="1" />'
Add-Line '    <StandardDirectory Id="ProgramFiles64Folder">'
Add-Line '      <Directory Id="INSTALLFOLDER" Name="ZungMail" />'
Add-Line '    </StandardDirectory>'
Add-Line '    <StandardDirectory Id="ProgramMenuFolder">'
Add-Line '      <Directory Id="ProgramMenuDir" Name="ZungMail" />'
Add-Line '    </StandardDirectory>'
Add-Line '    <StandardDirectory Id="DesktopFolder" />'
Add-Line '    <Feature Id="MainFeature" Title="ZungMail" Level="1">'
Add-Line '      <ComponentGroupRef Id="AppFiles" />'
Add-Line '      <ComponentRef Id="StartMenuShortcut" />'
Add-Line '      <ComponentRef Id="DesktopShortcut" />'
Add-Line '    </Feature>'
Add-Line '    <!-- 只在本机的正常安装/升级里启动客户端：卸载（REMOVE=ALL）与静默安装（UILevel=2）都不启动 -->'
Add-Line '    <InstallExecuteSequence>'
Add-Line '      <Custom Action="LaunchZungMail" After="InstallFinalize" Condition="LAUNCHAPP = 1 AND NOT REMOVE~=&quot;ALL&quot; AND UILevel &gt; 2" />'
Add-Line '    </InstallExecuteSequence>'
Add-Line '  </Package>'
Add-Line ''
Add-Line '  <Fragment>'
Add-Line '    <DirectoryRef Id="INSTALLFOLDER">'
Write-Node $root 3
Add-Line '    </DirectoryRef>'
Add-Line '    <ComponentGroup Id="AppFiles">'
$componentRefs = [System.Collections.Generic.List[string]]::new()
function Add-ComponentRefs([object]$node) {
    foreach ($file in $node.Files) { $componentRefs.Add($file.Component) }
    foreach ($child in $node.Children.Values) { Add-ComponentRefs $child }
}
Add-ComponentRefs $root
foreach ($id in $componentRefs) {
    Add-Line "      <ComponentRef Id=""$id"" />"
}
Add-Line '    </ComponentGroup>'
Add-Line '  </Fragment>'
Add-Line ''
Add-Line '  <Fragment>'
$startMenuGuid = New-StableGuid 'ZungMail/start-menu'
Add-Line ('    <Component Id="StartMenuShortcut" Directory="ProgramMenuDir" Guid="' + $startMenuGuid + '">')
Add-Line '      <Shortcut Id="StartMenuShortcut" Name="ZungMail" Description="ZungMail 邮件客户端" Target="[INSTALLFOLDER]ZungMail.exe" WorkingDirectory="INSTALLFOLDER" Icon="ZungMailIcon" />'
Add-Line '      <RemoveFolder Id="RemoveProgramMenuDir" On="uninstall" />'
Add-Line '      <RegistryValue Root="HKLM" Key="Software\ZungMail" Name="InstallLocation" Type="string" Value="[INSTALLFOLDER]" KeyPath="yes" />'
Add-Line '    </Component>'
Add-Line '  </Fragment>'
Add-Line ''
Add-Line '  <Fragment>'
$desktopGuid = New-StableGuid 'ZungMail/desktop'
Add-Line ('    <Component Id="DesktopShortcut" Directory="DesktopFolder" Guid="' + $desktopGuid + '">')
Add-Line '      <Shortcut Id="DesktopShortcut" Name="ZungMail" Description="ZungMail 邮件客户端" Target="[INSTALLFOLDER]ZungMail.exe" WorkingDirectory="INSTALLFOLDER" Icon="ZungMailIcon" />'
Add-Line '      <RegistryValue Root="HKLM" Key="Software\ZungMail" Name="DesktopShortcut" Type="integer" Value="1" KeyPath="yes" />'
Add-Line '    </Component>'
Add-Line '  </Fragment>'
Add-Line ''
Add-Line '  <Fragment>'
Add-Line '    <!-- Type 34（目录 + 命令行）：用 explorer.exe 转发启动，避免客户端继承管理员令牌 ——'
Add-Line '         提权进程会被 UIPI 挡住拖放（从 Outlook 拖邮件、往桌面拖附件都会失效）。asyncNoWait 让安装不等待客户端退出 -->'
Add-Line '    <CustomAction Id="LaunchZungMail" Directory="WindowsFolder" ExeCommand="&quot;[WindowsFolder]explorer.exe&quot; &quot;[INSTALLFOLDER]ZungMail.exe&quot;" Execute="immediate" Impersonate="yes" Return="asyncNoWait" />'
Add-Line '  </Fragment>'
Add-Line '</Wix>'

$outputDir = Split-Path -Parent $OutputFile
if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}
[System.IO.File]::WriteAllText($OutputFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "生成 MSI 源：$OutputFile（$($allFiles.Count) 个文件）"
