#Requires -Version 7.0
[CmdletBinding()]
param([switch]$NativeFixtures, [switch]$LiveLocal)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    function Check-Command([string[]]$Arguments) {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet check failed with exit code $LASTEXITCODE" }
    }
    Check-Command @('restore', 'Clicky.slnx')
    Check-Command @('build', 'Clicky.slnx', '--configuration', 'Release', '--no-restore', '--nologo')
    Check-Command @('test', 'Clicky.slnx', '--configuration', 'Release', '--no-build', '--results-directory', 'artifacts/test-results', '--logger', 'trx;LogFilePrefix=verify')
    Check-Command @('format', 'Clicky.slnx', '--no-restore', '--verify-no-changes')
    Check-Command @('run', '--project', 'tests/Clicky.Routing.UiTests', '--configuration', 'Release', '--no-build', '--', 'artifacts/routing-ui')
    Check-Command @('run', '--project', 'scripts/settings-controls-smoke', '--configuration', 'Release', '--', 'artifacts/settings-controls')
    if ($NativeFixtures) {
        Check-Command @('run', '--project', 'tests/Clicky.Native.Tests', '--configuration', 'Release', '--no-build', '--', '--speech')
        Check-Command @('run', '--project', 'tests/Clicky.Native.Tests', '--configuration', 'Release', '--no-build', '--', '--render')
        Check-Command @('run', '--project', 'tests/Clicky.Connectors.UiTests', '--configuration', 'Release', '--no-build', '--', 'artifacts/connector-ui')
    }
    if ($LiveLocal) {
        $executable = Join-Path $workspace 'src/Clicky.Windows/bin/Release/net10.0-windows10.0.19041.0/HeyBuddy.exe'
        $output = Join-Path $workspace ('artifacts/live-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        $process = Start-Process -FilePath $executable -ArgumentList @('--self-test', '--live', '--output', ('"' + $output + '"')) -WindowStyle Hidden -PassThru
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Local app validation failed. See $output/ui-result.json" }
    }
} finally { Pop-Location }
