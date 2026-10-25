# Compatibility shims for the .NET CLI.
#
# On some Windows machines every `dotnet build/test/publish/tool` command dies with
# 0xC0000005 (System.AccessViolationException) inside the CLI's background workload
# advertisement thread: it calls ServiceController.GetServices(), and .NET's
# EnumServicesStatusEx marshalling faults on one of the local service records.
# The crash kills the process even when the actual work already finished.
#
# The shims below keep the normal CLI path on healthy machines and transparently
# fall back to `dotnet exec <sdk>\MSBuild.dll` (or the cached WiX tool dll) when the
# CLI crashes with that specific exit code. No admin rights required.
#
# NOTE: this file is intentionally ASCII-only so Windows PowerShell 5.1 parses it
# correctly regardless of the default ANSI code page.

Set-StrictMode -Version Latest

# Repo root captured at dot-source time: $PSScriptRoot inside a later function call
# would resolve to the caller script instead of this helper.
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

# 0xC0000005 as seen through $LASTEXITCODE (signed int) and as unsigned.
$script:DotNetCrashCodes = @(-1073741819, 3221225477)

function Test-DotNetCliCrash {
    param([int]$ExitCode)
    return ($script:DotNetCrashCodes -contains $ExitCode)
}

function Get-DotNetSdkPath {
    # Pick the newest installed SDK without asking the CLI (which may be broken).
    $root = Join-Path $env:ProgramFiles 'dotnet\sdk'
    if (-not (Test-Path -LiteralPath $root)) {
        throw "dotnet SDK directory not found: $root"
    }

    $candidates = @(Get-ChildItem -LiteralPath $root -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending)

    if ($candidates.Count -eq 0) { throw "No installed SDK under $root." }

    # Prefer the 8.x band (project target framework), otherwise take the newest.
    $preferred = $candidates | Where-Object { $_.Name -like '8.*' } | Select-Object -First 1
    if ($preferred) { return $preferred.FullName }
    return $candidates[0].FullName
}

function Invoke-MSBuildCompat {
    <#
        Runs an MSBuild target without the dotnet CLI front end.
        -Properties accepts a hashtable translated to -p:Key=Value.
    #>
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Target,
        [string]$Configuration = 'Release',
        [hashtable]$Properties = @{},
        [switch]$Restore,
        [switch]$Quiet
    )

    $msbuild = Join-Path (Get-DotNetSdkPath) 'MSBuild.dll'
    if (-not (Test-Path -LiteralPath $msbuild)) { throw "MSBuild.dll not found: $msbuild" }

    $arguments = @('exec', $msbuild, $Project, "-t:$Target", "-p:Configuration=$Configuration")
    foreach ($key in $Properties.Keys) {
        $arguments += "-p:$key=$($Properties[$key])"
    }
    if ($Restore) { $arguments += '-restore' }
    $arguments += @('-m:1')
    $arguments += @('-v:' + $(if ($Quiet) { 'q' } else { 'm' }))

    Write-Host "    (fallback) dotnet exec MSBuild.dll -t:$Target" -ForegroundColor DarkYellow
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed (exit $LASTEXITCODE)." }
}

function Invoke-DotNetPublishCompat {
    <#
        dotnet publish with automatic MSBuild fallback. Extra -p: properties are
        passed through the hashtable so callers can keep their existing arguments.
    #>
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$PublishDir,
        [string]$Configuration = 'Release',
        [string]$Runtime = 'win-x64',
        [bool]$SelfContained = $true,
        [hashtable]$ExtraProperties = @{},
        [string[]]$ExtraArguments = @()
    )

    $arguments = @(
        'publish', $Project,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
        '-o', $PublishDir,
        '-p:PlatformTarget=x64',
        '-p:Platform=x64'
    )
    $arguments += $ExtraArguments

    & dotnet @arguments
    $code = $LASTEXITCODE
    if ($code -eq 0) { return }
    if (-not (Test-DotNetCliCrash $code)) { throw "dotnet publish failed (exit $code)." }

    Write-Warning "dotnet CLI crashed (0xC0000005, system service enumeration). Falling back to MSBuild."

    $properties = @{
        RuntimeIdentifier = $Runtime
        SelfContained     = $SelfContained.ToString().ToLowerInvariant()
        PlatformTarget    = 'x64'
        Platform          = 'x64'
        PublishDir        = $PublishDir
    }
    foreach ($key in $ExtraProperties.Keys) { $properties[$key] = $ExtraProperties[$key] }

    Invoke-MSBuildCompat -Project $Project -Target 'Publish' -Configuration $Configuration -Properties $properties -Restore
}

function Find-WixToolDll {
    param([string]$ManifestPath)

    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        $ManifestPath = Join-Path $script:RepoRoot '.config\dotnet-tools.json'
    }
    if (-not (Test-Path -LiteralPath $ManifestPath)) { return $null }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $version = $manifest.tools.wix.version
    if ([string]::IsNullOrWhiteSpace($version)) { return $null }

    $packageDir = Join-Path $env:USERPROFILE ".nuget\packages\wix\$version\tools"
    if (-not (Test-Path -LiteralPath $packageDir)) { return $null }

    $dll = Get-ChildItem -LiteralPath $packageDir -Recurse -Filter 'wix.dll' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($dll) { return $dll.FullName }
    return $null
}

function Invoke-DotNetToolRestoreCompat {
    & dotnet tool restore
    $code = $LASTEXITCODE
    if ($code -eq 0) { return }
    if (-not (Test-DotNetCliCrash $code)) { throw "dotnet tool restore failed (exit $code)." }

    if (Find-WixToolDll) {
        Write-Warning "dotnet CLI crashed, but the WiX tool is already in the NuGet cache; skipping tool restore."
        return
    }

    throw "dotnet tool restore crashed and no cached WiX tool was found. Run dotnet tool restore on a healthy machine and copy ~\.nuget\packages\wix back."
}

function Invoke-WixCompat {
    <# dotnet wix ... with fallback to `dotnet exec wix.dll ...`. #>
    param([Parameter(Mandatory)][string[]]$WixArguments)

    & dotnet wix @WixArguments
    $code = $LASTEXITCODE
    if ($code -eq 0) { return }
    if (-not (Test-DotNetCliCrash $code)) { throw "wix failed (exit $code)." }

    $dll = Find-WixToolDll
    if (-not $dll) { throw "dotnet CLI crashed and cached wix.dll was not found." }

    Write-Warning "dotnet CLI crashed (0xC0000005). Falling back to dotnet exec wix.dll."
    & dotnet exec $dll @WixArguments
    if ($LASTEXITCODE -ne 0) { throw "wix fallback failed (exit $LASTEXITCODE)." }
}
