param([string]$Configuration = "Debug")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$raw = & .\Send-StrideBridge.ps1 -Operation build_game -ArgumentsJson ('{"configuration":"' + $Configuration + '"}') -TimeoutMs 590000
$json = $raw | ConvertFrom-Json
Write-Output ("success=" + $json.data.success + " exitCode=" + $json.data.exitCode + " elapsed=" + $json.data.elapsedMilliseconds + "ms")
if (-not $json.data.success) {
    $errors = ($json.data.output -split "`n") | Where-Object { $_ -match 'error CS|error MSB|Se produjo un error' } | Select-Object -First 10
    if ($errors) { Write-Output "--- compiler errors ---"; $errors }
    else { Write-Output ($json.data.output | Select-Object -Last 15) }
}
