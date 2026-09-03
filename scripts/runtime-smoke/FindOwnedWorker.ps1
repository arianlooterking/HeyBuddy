param([Parameter(Mandatory)][ValidateRange(1024,65535)][int]$Port)
$ErrorActionPreference = 'Stop'
$listeners = @(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen)
$owners = @($listeners.OwningProcess | Select-Object -Unique)
if ($owners.Count -ne 1) { throw 'Expected exactly one loopback worker listener.' }
$owner = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $owners[0])
if (-not $owner) { throw 'Listener process disappeared.' }
[pscustomobject]@{
    Pid = [int]$owner.ProcessId
    ParentPid = [int]$owner.ParentProcessId
    ExecutablePath = $owner.ExecutablePath
    LocalAddress = '127.0.0.1'
    Port = $Port
} | ConvertTo-Json -Compress
