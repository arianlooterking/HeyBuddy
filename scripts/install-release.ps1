#Requires -Version 7.0
<#
.SYNOPSIS
Validates and installs the repository's HeyBuddy 0.2.2 release while preserving local user data.

.DESCRIPTION
Run from a normal, unelevated PowerShell 7 session after scripts/release.ps1 has produced the
0.2.2 artifacts. The installer is per-user. This helper never changes settings or credentials.

.PARAMETER DryRun
Validates release metadata, installer and payload hashes, inventories retained data, and reports
matching installed processes. It does not stop processes, create a backup, install, or launch.

.EXAMPLE
pwsh -NoProfile -File scripts/install-release.ps1 -DryRun

.EXAMPLE
pwsh -NoProfile -File scripts/install-release.ps1
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedVersion = '0.2.2'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts\release'))
$releaseManifest = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'release.json'))
$payloadRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'HeyBuddy'))
$installer = [IO.Path]::GetFullPath((Join-Path $releaseRoot "HeyBuddy-$expectedVersion-Setup-x64.exe"))
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\HeyBuddy'))
$installedExe = [IO.Path]::GetFullPath((Join-Path $installRoot 'HeyBuddy.exe'))
$dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ClickyLocal'))
$reportName = if ($DryRun) { "dry-run-$expectedVersion.json" } else { "upgrade-$expectedVersion.json" }
$reportPath = [IO.Path]::GetFullPath((Join-Path $releaseRoot $reportName))
$utf8 = [Text.UTF8Encoding]::new($false)
$excludedDataFolders = @('Models', 'Runtime', 'Logs', 'Backups')

function Assert-PathWithin([string]$Candidate, [string]$Parent, [switch]$AllowEqual) {
    $resolvedCandidate = [IO.Path]::GetFullPath($Candidate)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($AllowEqual -and $resolvedCandidate.Equals($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        return $resolvedCandidate
    }
    $prefix = $resolvedParent + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedCandidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a path outside $resolvedParent`: $resolvedCandidate"
    }
    return $resolvedCandidate
}

function Assert-ExactPath([string]$Candidate, [string]$Expected, [string]$Description) {
    $resolved = [IO.Path]::GetFullPath($Candidate)
    $resolvedExpected = [IO.Path]::GetFullPath($Expected)
    if (-not $resolved.Equals($resolvedExpected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description resolved to an unexpected location: $resolved"
    }
    return $resolved
}

function Get-RequiredJson([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Description is missing: $Path" }
    try { return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json }
    catch { throw "$Description is not valid JSON: $($_.Exception.Message)" }
}

function Get-Sha256([string]$Path, [switch]$AllowConcurrentWrites) {
    if (-not $AllowConcurrentWrites) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-FileInventory([string]$Base, [IO.FileInfo[]]$Files, [switch]$AllowConcurrentWrites) {
    @($Files | Sort-Object FullName | ForEach-Object {
        $fullPath = Assert-PathWithin $_.FullName $Base
        [pscustomobject]@{
            Path = [IO.Path]::GetRelativePath($Base, $fullPath).Replace('\', '/')
            Hash = Get-Sha256 $fullPath -AllowConcurrentWrites:$AllowConcurrentWrites
            Bytes = $_.Length
        }
    })
}

function Get-RetainedDataItems {
    if (-not (Test-Path -LiteralPath $dataRoot -PathType Container)) { return @() }
    $rootInfo = Get-Item -LiteralPath $dataRoot -Force
    if (($rootInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The data directory is a reparse point; back it up manually before installing: $dataRoot"
    }
    $items = @(Get-ChildItem -LiteralPath $dataRoot -Force | Where-Object { $_.Name -notin $excludedDataFolders })
    foreach ($item in $items) {
        Assert-PathWithin $item.FullName $dataRoot | Out-Null
        $tree = @($item)
        if ($item.PSIsContainer) { $tree += @(Get-ChildItem -LiteralPath $item.FullName -Force -Recurse) }
        $reparse = $tree | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
        if ($null -ne $reparse) {
            throw "Retained user data contains a reparse point; back it up manually before installing: $($reparse.FullName)"
        }
    }
    return $items
}

function Get-RetainedDataInventory([IO.FileSystemInfo[]]$Items, [switch]$AllowConcurrentWrites) {
    $files = @($Items | ForEach-Object {
        if ($_.PSIsContainer) { Get-ChildItem -LiteralPath $_.FullName -Force -Recurse -File }
        else { $_ }
    })
    return Get-FileInventory $dataRoot $files -AllowConcurrentWrites:$AllowConcurrentWrites
}

function Read-HashManifest([string]$Path, [string]$Base) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "SHA-256 manifest is missing: $Path" }
    $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $Path -Encoding utf8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw "Invalid SHA-256 manifest line in $Path" }
        $relative = $Matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($relative)) { throw "Rooted path in SHA-256 manifest: $relative" }
        $resolved = Assert-PathWithin (Join-Path $Base $relative) $Base
        $canonical = [IO.Path]::GetRelativePath($Base, $resolved).Replace('\', '/')
        if (-not $entries.TryAdd($canonical, $Matches[1].ToLowerInvariant())) { throw "Duplicate SHA-256 manifest path: $canonical" }
    }
    return $entries
}

function Assert-PayloadIntegrity {
    $payloadManifestPath = Join-Path $payloadRoot 'SHA256SUMS.txt'
    $expected = Read-HashManifest $payloadManifestPath $payloadRoot
    $actualFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Force -Recurse -File | Where-Object { $_.FullName -ne $payloadManifestPath })
    if ($expected.Count -ne $actualFiles.Count) {
        throw "Payload manifest lists $($expected.Count) files but the payload contains $($actualFiles.Count)."
    }
    foreach ($file in $actualFiles) {
        $relative = [IO.Path]::GetRelativePath($payloadRoot, $file.FullName).Replace('\', '/')
        if (-not $expected.ContainsKey($relative)) { throw "Payload file is not listed in its SHA-256 manifest: $relative" }
        $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $expected[$relative]) { throw "Payload SHA-256 mismatch: $relative" }
    }
    return $actualFiles.Count + 1
}

function Assert-InstallerIntegrity {
    $artifactManifest = Read-HashManifest (Join-Path $releaseRoot 'SHA256SUMS.txt') $releaseRoot
    $relative = [IO.Path]::GetRelativePath($releaseRoot, $installer).Replace('\', '/')
    if (-not $artifactManifest.ContainsKey($relative)) { throw "The installer is not listed in artifacts/release/SHA256SUMS.txt: $relative" }
    $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $artifactManifest[$relative]) { throw 'The installer SHA-256 does not match the release manifest.' }
    return $actualHash
}

function Get-InstalledAppProcesses {
    $matches = @()
    foreach ($candidate in @(Get-CimInstance Win32_Process -Filter "Name = 'HeyBuddy.exe'")) {
        if ([string]::IsNullOrWhiteSpace($candidate.ExecutablePath)) { continue }
        try { $path = [IO.Path]::GetFullPath($candidate.ExecutablePath) }
        catch { continue }
        if ($path.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)) {
            $matches += $candidate
        }
    }
    return $matches
}

function Stop-InstalledApp([object[]]$Processes) {
    $stopped = @()
    foreach ($candidate in $Processes) {
        # Re-query immediately before stopping so a recycled process ID cannot target another executable.
        $current = Get-CimInstance Win32_Process -Filter ("ProcessId = " + [int]$candidate.ProcessId) -ErrorAction SilentlyContinue
        if ($null -eq $current -or [string]::IsNullOrWhiteSpace($current.ExecutablePath)) { continue }
        $verifiedPath = [IO.Path]::GetFullPath($current.ExecutablePath)
        if (-not $verifiedPath.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Process $($candidate.ProcessId) changed identity; it was not stopped."
        }
        $process = Get-Process -Id ([int]$candidate.ProcessId) -ErrorAction Stop
        if ($process.CloseMainWindow()) {
            $process.WaitForExit(10000) | Out-Null
        }
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -ErrorAction Stop
            Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
        if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) { throw "Installed HeyBuddy process $($process.Id) did not exit." }
        $stopped += $process.Id
    }
    return $stopped
}

function Assert-RetainedData([object[]]$Before) {
    @($Before | ForEach-Object {
        $candidate = Assert-PathWithin (Join-Path $dataRoot $_.Path) $dataRoot
        $exists = Test-Path -LiteralPath $candidate -PathType Leaf
        $afterHash = if ($exists) { (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        [pscustomobject]@{ Path = $_.Path; BeforeHash = $_.Hash; AfterHash = $afterHash; Match = $exists -and $afterHash -eq $_.Hash }
    })
}

function Assert-InstalledPayload {
    $results = @(Get-ChildItem -LiteralPath $payloadRoot -Force -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($payloadRoot, $_.FullName)
        $target = Assert-PathWithin (Join-Path $installRoot $relative) $installRoot
        $expectedHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $exists = Test-Path -LiteralPath $target -PathType Leaf
        $actualHash = if ($exists) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        [pscustomobject]@{ Path = $relative.Replace('\', '/'); Match = $exists -and $actualHash -eq $expectedHash }
    })
    $mismatches = @($results | Where-Object { -not $_.Match })
    if ($mismatches.Count -gt 0) { throw "Installed payload differs from the release in $($mismatches.Count) file(s)." }
    return $results
}

Assert-ExactPath $releaseRoot (Join-Path $workspaceRoot 'artifacts\release') 'Release directory' | Out-Null
Assert-PathWithin $releaseManifest $releaseRoot | Out-Null
Assert-PathWithin $payloadRoot $releaseRoot | Out-Null
Assert-PathWithin $installer $releaseRoot | Out-Null
Assert-ExactPath $installRoot (Join-Path $env:LOCALAPPDATA 'Programs\HeyBuddy') 'Install directory' | Out-Null
Assert-ExactPath $dataRoot (Join-Path $env:LOCALAPPDATA 'ClickyLocal') 'Data directory' | Out-Null

$report = [ordered]@{
    Version = $expectedVersion
    Mode = if ($DryRun) { 'DryRun' } else { 'Install' }
    StartedAt = [DateTimeOffset]::Now.ToString('O')
    CompletedAt = $null
    Success = $false
    ReleaseManifest = $releaseManifest
    Installer = $installer
    InstallerSha256 = $null
    Payload = $payloadRoot
    PayloadFilesValidated = 0
    InstallDirectory = $installRoot
    DataDirectory = $dataRoot
    InstalledBefore = Test-Path -LiteralPath $installedExe -PathType Leaf
    MatchingProcessIds = @()
    StoppedProcessIds = @()
    BackupDirectory = $null
    RetainedDataBefore = @()
    RetainedDataAfterInstall = @()
    InstalledPayloadFilesVerified = 0
    LaunchedProcessId = $null
    MutationsPerformed = $false
    Error = $null
}

try {
    $metadata = Get-RequiredJson $releaseManifest 'Top-level release manifest'
    if ([string]$metadata.version -cne $expectedVersion) { throw "Release manifest version must be exactly $expectedVersion." }
    if ($null -ne $metadata.product -and [string]$metadata.product -cne 'HeyBuddy') { throw 'Release manifest product must be HeyBuddy.' }
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "The $expectedVersion installer is missing: $installer" }
    if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) { throw "The release payload is missing: $payloadRoot" }
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot 'HeyBuddy.exe') -PathType Leaf)) { throw 'The release payload does not contain HeyBuddy.exe.' }
    $payloadMetadata = Get-RequiredJson (Join-Path $payloadRoot 'release.json') 'Payload release manifest'
    if ([string]$payloadMetadata.version -cne $expectedVersion) { throw "Payload release version must be exactly $expectedVersion." }
    $report.PayloadFilesValidated = Assert-PayloadIntegrity
    $report.InstallerSha256 = Assert-InstallerIntegrity

    $running = @(Get-InstalledAppProcesses)
    $report.MatchingProcessIds = @($running | ForEach-Object { [int]$_.ProcessId })

    if ($DryRun) {
        $retainedItems = @(Get-RetainedDataItems)
        $before = @(Get-RetainedDataInventory $retainedItems -AllowConcurrentWrites)
        $report.RetainedDataBefore = $before
        $report.Success = $true
        Write-Output "Dry-run validation passed for HeyBuddy $expectedVersion. No process was stopped, no backup or installation was performed, and no app was launched."
    }
    else {
        $report.MutationsPerformed = $true
        $report.StoppedProcessIds = @(Stop-InstalledApp $running)
        $exitDeadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $remainingInstalledProcesses = @(Get-InstalledAppProcesses)
            if ($remainingInstalledProcesses.Count -eq 0) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $exitDeadline)
        if ($remainingInstalledProcesses.Count -ne 0) { throw 'The installed HeyBuddy process is still running.' }

        $retainedItems = @(Get-RetainedDataItems)
        $before = @(Get-RetainedDataInventory $retainedItems)
        $report.RetainedDataBefore = $before
        New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
        $backupRoot = Assert-PathWithin (Join-Path $dataRoot 'Backups') $dataRoot
        New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
        $backup = Assert-PathWithin (Join-Path $backupRoot ("before-$expectedVersion-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))) $backupRoot
        New-Item -ItemType Directory -Path $backup | Out-Null
        foreach ($item in $retainedItems) { Copy-Item -LiteralPath $item.FullName -Destination $backup -Force -Recurse }
        $report.BackupDirectory = $backup
        $backupFiles = @(Get-ChildItem -LiteralPath $backup -Force -Recurse -File)
        $backupInventory = @(Get-FileInventory $backup $backupFiles)
        if ($backupInventory.Count -ne $before.Count) { throw 'The backup file count does not match retained user data.' }
        foreach ($entry in $before) {
            $copy = $backupInventory | Where-Object Path -eq $entry.Path | Select-Object -First 1
            if ($null -eq $copy -or $copy.Hash -ne $entry.Hash) { throw "Backup verification failed: $($entry.Path)" }
        }

        $installLog = Assert-PathWithin (Join-Path $releaseRoot "install-$expectedVersion.log") $releaseRoot
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=desktopicon', "/LOG=`"$installLog`"")
        $installerProcess = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($installerProcess.ExitCode -ne 0) { throw "Installer failed with exit code $($installerProcess.ExitCode)." }
        if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) { throw "Installer did not create the expected executable: $installedExe" }

        $preserved = @(Assert-RetainedData $before)
        $report.RetainedDataAfterInstall = $preserved
        $changed = @($preserved | Where-Object { -not $_.Match })
        if ($changed.Count -gt 0) { throw "Installation changed $($changed.Count) retained user data file(s). The verified backup remains available." }

        $payloadResults = @(Assert-InstalledPayload)
        $report.InstalledPayloadFilesVerified = $payloadResults.Count
        $launched = Start-Process -FilePath $installedExe -WorkingDirectory $installRoot -PassThru
        $report.LaunchedProcessId = $launched.Id
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 200
            $launched.Refresh()
        } until ($launched.HasExited -or [DateTime]::UtcNow -ge $deadline -or (Get-InstalledAppProcesses | Where-Object ProcessId -eq $launched.Id))
        if ($launched.HasExited) { throw "Installed HeyBuddy exited during startup with code $($launched.ExitCode)." }
        $live = Get-InstalledAppProcesses | Where-Object ProcessId -eq $launched.Id | Select-Object -First 1
        if ($null -eq $live) { throw 'The launched process executable path could not be verified as the installed HeyBuddy app.' }
        $report.Success = $true
        Write-Output "HeyBuddy $expectedVersion installed, preserved data verified, payload verified, and the installed app launched normally."
    }
}
catch {
    $report.Error = $_.Exception.Message
    throw
}
finally {
    $report.CompletedAt = [DateTimeOffset]::Now.ToString('O')
    if (Test-Path -LiteralPath $releaseRoot -PathType Container) {
        [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), $utf8)
    }
}
