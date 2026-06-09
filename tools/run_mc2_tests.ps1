# Runs the MagicaCloth2 characterization test suite end to end:
#   refresh -> wait for compile -> exec play-mode run -> wait for results.json -> print it.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/run_mc2_tests.ps1            # full cycle
#   powershell -ExecutionPolicy Bypass -File tools/run_mc2_tests.ps1 -NoRefresh # skip recompile
param([switch]$NoRefresh, [string]$Only = '')

function Cmd($c) {
    try { return (& "$PSScriptRoot/unity_cmd.ps1" -cmd $c) 2>$null } catch { return 'down' }
}

$resultsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'ExportedAssets/test-results'
$filterPath = Join-Path $resultsDir 'filter.txt'
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
if ($Only -ne '') { Set-Content -Path $filterPath -Value $Only -NoNewline }
elseif (Test-Path $filterPath) { Remove-Item $filterPath -Force }

if (-not $NoRefresh) {
    Cmd 'refresh' | Out-Null
    Start-Sleep -Seconds 6
    $log = 'compiling'
    for ($i = 0; $i -lt 40; $i++) {
        $log = Cmd 'log'
        if ($log -and $log -ne 'compiling' -and $log -ne 'down') { break }
        Start-Sleep -Seconds 3
    }
    if ($log -like 'ERRORS*') { Write-Output "COMPILE $log"; exit 1 }
}

$results = Join-Path (Split-Path $PSScriptRoot -Parent) 'ExportedAssets/test-results/results.json'
if (Test-Path $results) { Remove-Item $results -Force }

Cmd "exec:MagicaCloth2.Tests.TestRunner.Run" | Out-Null

for ($i = 0; $i -lt 45; $i++) {
    if (Test-Path $results) { break }
    Start-Sleep -Seconds 2
}

if (Test-Path $results) { Get-Content $results -Raw } else { Write-Output 'NO RESULTS (timeout)' }
