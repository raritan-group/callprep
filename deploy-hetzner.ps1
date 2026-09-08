# Deploy Call Prep to the Hetzner box (178.156.238.36) as systemd service `callprep` behind nginx (callprep.raritangroup.com).
# Usage:  pwsh -File .\deploy-hetzner.ps1            (build UI + publish API + upload + restart)
#         pwsh -File .\deploy-hetzner.ps1 -SkipUi    (API only)
#         pwsh -File .\deploy-hetzner.ps1 -WithModel (also upload models\ggml-base.en.bin, 148 MB — first deploy or model change)
# Secrets never travel with the deploy: the service reads /etc/callprep.env on the box (root-only, 600).
param([switch]$SkipUi, [switch]$WithModel)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$key  = "$env:USERPROFILE\.ssh\it_portal"
$host_ = 'root@178.156.238.36'
$ssh  = "ssh -i `"$key`" -o BatchMode=yes"

if (-not $SkipUi) {
    Push-Location "$root\CallPrep.Web"; npm run build | Out-Host; Pop-Location
    $ww = "$root\CallPrep.Api\wwwroot"
    if (Test-Path $ww) { Remove-Item -Recurse -Force $ww }
    Copy-Item -Recurse "$root\CallPrep.Web\dist" $ww
}

$out = "$root\publish_linux"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Push-Location "$root\CallPrep.Api"
dotnet publish -c Release -r linux-x64 --self-contained false -o $out -nologo -v q | Out-Host
Pop-Location
if (-not (Test-Path "$out\CallPrep.Api.dll")) { throw 'publish failed' }

# stage as a tarball: one scp, then swap on the box
$tar = "$env:TEMP\callprep_publish.tar.gz"
if (Test-Path $tar) { Remove-Item $tar }
tar -czf $tar -C $out .
scp -i $key -o BatchMode=yes $tar "${host_}:/tmp/callprep_publish.tar.gz" | Out-Host
if ($WithModel) { scp -i $key -o BatchMode=yes "$root\models\ggml-base.en.bin" "${host_}:/opt/callprep/models/ggml-base.en.bin" | Out-Host }

$remote = @'
set -e
systemctl stop callprep 2>/dev/null || true
mkdir -p /opt/callprep/models
find /opt/callprep -mindepth 1 -maxdepth 1 ! -name models -exec rm -rf {} +
tar -xzf /tmp/callprep_publish.tar.gz -C /opt/callprep
rm -f /tmp/callprep_publish.tar.gz
systemctl enable --now callprep
sleep 4
systemctl is-active callprep
curl -s http://127.0.0.1:5080/api/health; echo
'@
$remote | & ssh -i $key -o BatchMode=yes $host_ 'tr -d "\r" | bash -s'
Remove-Item $tar -ErrorAction SilentlyContinue
Write-Host "deployed. logs: ssh $host_ journalctl -u callprep -n 50"
