param(
    [Parameter(Mandatory = $true)][string]$ScriptFile
)

$ErrorActionPreference = 'Stop'
$script = Get-Content $ScriptFile -Raw
$arguments = @{ script = $script } | ConvertTo-Json -Compress
Set-Location $PSScriptRoot
& .\Send-StrideBridge.ps1 -Operation execute_script -ArgumentsJson $arguments -TimeoutMs 120000
