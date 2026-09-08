# CallPrep local runner (RG-IT tower). Loads secrets into this process, opens the SSH tunnel to the
# Hetzner reporting Postgres, then starts the API on http://localhost:5070.
# Usage:  pwsh -File .\run-local.ps1            (API only; run `npm run dev` in CallPrep.Web for the UI at :5173)
#         pwsh -File .\run-local.ps1 -Build     (build the React app into CallPrep.Api\wwwroot, then serve everything at :5070)
param([switch]$Build, [int]$TunnelPort = 15432)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 1. Secrets -> environment (decrypts in-process; nothing written to disk)
$py = @'
import sys; sys.path.insert(0, r"C:\Scripts\.secrets")
import secrets_loader as S
d = S.parse_env(S.decrypt_env_text())
for k in ("ANTHROPIC_API_KEY", "PG_PASSWORD_CALLPREP"):
    print(f"{k}={d[k]}")
'@
foreach ($line in (python -c $py)) {
    $k, $v = $line -split '=', 2
    [Environment]::SetEnvironmentVariable($k, $v, 'Process')
}
if (-not $env:ANTHROPIC_API_KEY -or -not $env:PG_PASSWORD_CALLPREP) { throw 'secrets not loaded' }

# 1b. Entra sign-in (shared employee app registration, same as RGCommerce). The client secret is read from the
#     Hetzner box's rgcommerce config over SSH so there is exactly one copy of it; it never lands on disk here.
$env:ENTRA_TENANT_ID = '6ea34d6a-c05b-4142-89e3-160ce1904606'
$env:ENTRA_CLIENT_ID = 'c69f5373-bbaa-4282-85b5-7a00c64aed26'
$env:ENTRA_CLIENT_SECRET = (ssh -i "$env:USERPROFILE\.ssh\it_portal" -o BatchMode=yes root@178.156.238.36 'grep "^ENTRA_CLIENT_SECRET=" /etc/callprep.env | cut -d= -f2-')
if (-not $env:ENTRA_CLIENT_SECRET) { throw 'ENTRA_CLIENT_SECRET not fetched from Hetzner (/etc/callprep.env)' }

# 2. SSH tunnel (idempotent: skip if the port is already listening)
$listening = Get-NetTCPConnection -LocalPort $TunnelPort -State Listen -ErrorAction SilentlyContinue
if (-not $listening) {
    $key = "$env:USERPROFILE\.ssh\it_portal"
    Start-Process -WindowStyle Hidden ssh -ArgumentList @('-N', '-L', "${TunnelPort}:127.0.0.1:5432", '-i', $key, '-o', 'BatchMode=yes', '-o', 'ServerAliveInterval=30', '-o', 'ExitOnForwardFailure=yes', 'root@178.156.238.36')
    Start-Sleep -Seconds 3
    if (-not (Get-NetTCPConnection -LocalPort $TunnelPort -State Listen -ErrorAction SilentlyContinue)) { throw "tunnel did not come up on $TunnelPort" }
    Write-Host "tunnel up on 127.0.0.1:$TunnelPort"
} else { Write-Host "tunnel already up on 127.0.0.1:$TunnelPort" }

# 3. Optional: build the UI into the API's wwwroot
if ($Build) {
    Push-Location "$root\CallPrep.Web"
    npm run build | Out-Host
    Pop-Location
    $ww = "$root\CallPrep.Api\wwwroot"
    if (Test-Path $ww) { Remove-Item -Recurse -Force $ww }
    Copy-Item -Recurse "$root\CallPrep.Web\dist" $ww
    Write-Host "UI built into $ww"
}

# 4. API
$env:CALLPREP_PG_PORT = "$TunnelPort"
$env:ASPNETCORE_URLS = 'http://localhost:5070'
Push-Location "$root\CallPrep.Api"
dotnet run --no-launch-profile
Pop-Location
