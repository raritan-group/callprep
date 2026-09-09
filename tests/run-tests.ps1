# Call Prep test runner (RG-IT). Loads secrets into this process, opens the SSH tunnel to the Hetzner reportsdb, runs the suite.
#
#   pwsh -File tests\run-tests.ps1                      unit + DB integration + HTTP e2e (no model calls, free, ~1 min)
#   pwsh -File tests\run-tests.ps1 -Unit                unit tests only (no tunnel, no secrets)
#   pwsh -File tests\run-tests.ps1 -Live                adds the model-in-the-loop chat tests (spends Anthropic credits)
#   pwsh -File tests\run-tests.ps1 -Customers 'Buist;Coppola;Middlesex Water'      demo customer set for the sweeps
#   pwsh -File tests\run-tests.ps1 -Live -LiveCustomers 'Buist;Hungerford'         customers for the chat sweep (default: first 3)
#   pwsh -File tests\run-tests.ps1 -Filter 'FullyQualifiedName~ScopingTests'       any dotnet test filter
#
# Results: tests\results\<timestamp>.trx plus the console log (tool timings, answers, transcripts).
param(
    [switch]$Unit,
    [switch]$Live,
    [string]$Customers,
    [string]$LiveCustomers,
    [string]$Filter,
    [string]$Model,
    [int]$TunnelPort = 15432
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $Unit) {
    # secrets -> process env (same loader as run-local.ps1; nothing written to disk)
    $py = @'
import sys; sys.path.insert(0, r"C:\Scripts\.secrets")
import secrets_loader as S
d = S.parse_env(S.decrypt_env_text())
for k in ("ANTHROPIC_API_KEY", "PG_PASSWORD_CALLPREP"):
    print(f"{k}={d[k]}")
'@
    foreach ($line in (python -c $py)) { $k, $v = $line -split '=', 2; [Environment]::SetEnvironmentVariable($k, $v, 'Process') }
    if (-not $env:ANTHROPIC_API_KEY -or -not $env:PG_PASSWORD_CALLPREP) { throw 'secrets not loaded' }

    if (-not (Get-NetTCPConnection -LocalPort $TunnelPort -State Listen -ErrorAction SilentlyContinue)) {
        $key = "$env:USERPROFILE\.ssh\it_portal"
        Start-Process -WindowStyle Hidden ssh -ArgumentList @('-N', '-L', "${TunnelPort}:127.0.0.1:5432", '-i', $key, '-o', 'BatchMode=yes', '-o', 'ServerAliveInterval=30', '-o', 'ExitOnForwardFailure=yes', 'root@178.156.238.36')
        Start-Sleep -Seconds 3
        if (-not (Get-NetTCPConnection -LocalPort $TunnelPort -State Listen -ErrorAction SilentlyContinue)) { throw "tunnel did not come up on $TunnelPort" }
        Write-Host "tunnel up on 127.0.0.1:$TunnelPort"
    } else { Write-Host "tunnel already up on 127.0.0.1:$TunnelPort" }
    $env:CALLPREP_PG_PORT = "$TunnelPort"
}

if ($Live) { $env:CALLPREP_LIVE = '1' } else { Remove-Item Env:CALLPREP_LIVE -ErrorAction SilentlyContinue }
if ($Customers) { $env:CALLPREP_TEST_CUSTOMERS = $Customers }
if ($LiveCustomers) { $env:CALLPREP_LIVE_CUSTOMERS = $LiveCustomers }
if ($Model) { $env:CALLPREP_MODEL = $Model }

$filter = if ($Filter) { $Filter } elseif ($Unit) { 'FullyQualifiedName~CallPrep.Tests.Unit' } else { '' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force $results | Out-Null

$args = @('test', (Join-Path $root 'CallPrep.Tests\CallPrep.Tests.csproj'), '--logger', "trx;LogFileName=$stamp.trx", '--logger', 'console;verbosity=normal', '--results-directory', $results)
if ($filter) { $args += @('--filter', $filter) }
Write-Host "dotnet $($args -join ' ')"
& dotnet @args
exit $LASTEXITCODE
