#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$')]
    [string]$Version = '0.2.1',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$PortableOnly,
    [string]$InnoCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts\release'))
$publishDirectory = Join-Path $releaseRoot 'HeyBuddy'
$project = Join-Path $workspaceRoot 'src\Clicky.Windows\Clicky.Windows.csproj'
$installerScript = Join-Path $workspaceRoot 'packaging\HeyBuddy.iss'
$releaseStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stagingDirectory = Join-Path $releaseRoot ('.staging-' + [guid]::NewGuid().ToString('N'))
$previousDirectory = Join-Path $releaseRoot ('.previous\' + $releaseStamp)
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-ReleasePath([string]$Candidate) {
    $resolved = [IO.Path]::GetFullPath($Candidate)
    $prefix = $releaseRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a release file operation outside artifacts/release: $resolved"
    }
    return $resolved
}

function Invoke-Checked([string]$Executable, [string[]]$ArgumentList) {
    & $Executable @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

function Preserve-Previous([string]$Candidate) {
    $resolved = Assert-ReleasePath $Candidate
    if (Test-Path -LiteralPath $resolved) {
        $preservedRoot = Assert-ReleasePath $previousDirectory
        New-Item -ItemType Directory -Force -Path $preservedRoot | Out-Null
        $destination = Assert-ReleasePath (Join-Path $preservedRoot ([IO.Path]::GetFileName($resolved)))
        # Both absolute paths are checked. Only generated release artifacts move; user data/models are outside this tree.
        Move-Item -LiteralPath $resolved -Destination $destination
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required to build the release.' }
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$assemblyNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/AssemblyName')
if ($null -eq $assemblyNode -or $assemblyNode.InnerText -ne 'HeyBuddy') {
    throw 'The Windows project must use AssemblyName HeyBuddy before packaging.'
}

if (-not $PortableOnly) {
    if (-not $InnoCompiler) {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
        throw 'Inno Setup 6 ISCC.exe was not found. Pass -InnoCompiler with its path, or use -PortableOnly.'
    }
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Assert-ReleasePath $stagingDirectory | Out-Null
Write-Host 'Publishing the self-contained Windows x64 application...'
Invoke-Checked 'dotnet' @(
    'publish', $project, '--configuration', $Configuration,
    '--runtime', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
    '-p:DebugSymbols=false', '-p:DebugType=None',
    "-p:Version=$Version", "-p:InformationalVersion=$Version", '--output', $stagingDirectory, '--nologo'
)
if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory 'HeyBuddy.exe') -PathType Leaf)) {
    throw 'Publish did not produce HeyBuddy.exe. The staging directory has been preserved for inspection.'
}

foreach ($name in @('README.md', 'LICENSE', 'LICENSE.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'THIRD_PARTY_NOTICES.md', 'NOTICE', 'NOTICE.md')) {
    $source = Join-Path $workspaceRoot $name
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $stagingDirectory $name) }
}
$documentationDirectory = Join-Path $stagingDirectory 'docs'
New-Item -ItemType Directory -Force -Path $documentationDirectory | Out-Null
foreach ($name in @('recovery.md', 'native.md', 'runtime.md', 'connectors.md', 'canvas-ui.md', 'validation.md', 'feature-matrix.md', 'refinement-2026-09-03.md')) {
    $source = Join-Path $workspaceRoot ('docs\' + $name)
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $documentationDirectory $name) }
}
$documentationImageDirectory = Join-Path $documentationDirectory 'images'
New-Item -ItemType Directory -Force -Path $documentationImageDirectory | Out-Null
$publicScreenshot = Join-Path $workspaceRoot 'docs\images\heybuddy-conversation.png'
if (Test-Path -LiteralPath $publicScreenshot -PathType Leaf) {
    Copy-Item -LiteralPath $publicScreenshot -Destination (Join-Path $documentationImageDirectory 'heybuddy-conversation.png')
}
$documentationEvidenceDirectory = Join-Path $documentationDirectory 'evidence'
New-Item -ItemType Directory -Force -Path $documentationEvidenceDirectory | Out-Null
$publicEvidence = Join-Path $workspaceRoot 'docs\evidence\2026-09-03-local-validation.json'
if (Test-Path -LiteralPath $publicEvidence -PathType Leaf) {
    Copy-Item -LiteralPath $publicEvidence -Destination (Join-Path $documentationEvidenceDirectory '2026-09-03-local-validation.json')
}
$releaseMetadata = [ordered]@{
    product = 'HeyBuddy'
    version = $Version
    runtime = 'win-x64'
    configuration = $Configuration
    selfContained = $true
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    applicationData = '%LOCALAPPDATA%\ClickyLocal'
    defaultModels = '%LOCALAPPDATA%\ClickyLocal\Models (or a user-selected folder)'
    modelDownloadsIncluded = $false
    codeSigned = $false
}
[IO.File]::WriteAllText((Join-Path $stagingDirectory 'release.json'), ($releaseMetadata | ConvertTo-Json), $utf8)
$fileHashes = foreach ($file in Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($stagingDirectory, $file.FullName).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[IO.File]::WriteAllLines((Join-Path $stagingDirectory 'SHA256SUMS.txt'), [string[]]$fileHashes, $utf8)

Preserve-Previous $publishDirectory
$resolvedStaging = Assert-ReleasePath $stagingDirectory
$resolvedPublish = Assert-ReleasePath $publishDirectory
Move-Item -LiteralPath $resolvedStaging -Destination $resolvedPublish

$portablePath = Join-Path $releaseRoot "HeyBuddy-$Version-win-x64.zip"
Preserve-Previous $portablePath
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host 'Creating the portable ZIP...'
[IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $portablePath, [IO.Compression.CompressionLevel]::Optimal, $true)
$outputs = @($portablePath)

if (-not $PortableOnly) {
    $installerPath = Join-Path $releaseRoot "HeyBuddy-$Version-Setup-x64.exe"
    Preserve-Previous $installerPath
    Write-Host 'Compiling the per-user installer...'
    Invoke-Checked $InnoCompiler @(
        "/DAppVersion=$Version", "/DAppSourceDir=$publishDirectory", "/DReleaseDir=$releaseRoot", $installerScript
    )
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'Inno Setup returned without the expected installer.' }
    $outputs += $installerPath
}

$outputHashes = foreach ($output in $outputs) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($output)
}
$manifest = Join-Path $releaseRoot 'SHA256SUMS.txt'
Preserve-Previous $manifest
[IO.File]::WriteAllLines($manifest, [string[]]$outputHashes, $utf8)
$topLevelMetadata = Join-Path $releaseRoot 'release.json'
Preserve-Previous $topLevelMetadata
[IO.File]::WriteAllText($topLevelMetadata, ($releaseMetadata | ConvertTo-Json), $utf8)
Write-Host 'Release artifacts created. No installer has been run and no account connection has been made.'
$outputs + $manifest + $topLevelMetadata | ForEach-Object { Write-Output $_ }
