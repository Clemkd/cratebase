# Rejoue la suite de bout en bout avec le stockage objet S3 (MinIO).
#
# Pendant de `postgres.ps1` pour l'autre moitié de la promesse d'évolutivité : passer du disque
# local à S3 doit rester un changement de configuration. Le jour où ce script casse, ce n'est plus
# vrai.
#
#   pwsh tests/minio.ps1

param(
    [int]$MinioPort = 61900,
    [int]$AppPort = 8090,
    [string]$Container = 'cratebase-minio'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path $PSScriptRoot -Parent

Write-Host "== MinIO ==" -ForegroundColor Cyan

if (-not (docker ps -a --filter "name=^$Container$" --format '{{.Names}}')) {
    docker run -d --name $Container -p "${MinioPort}:9000" `
        -e 'MINIO_ROOT_USER=cratebase' -e 'MINIO_ROOT_PASSWORD=cratebase-secret' `
        'minio/minio:latest' server /data | Out-Null
}
else {
    docker start $Container | Out-Null
}

Start-Sleep -Seconds 4
Write-Host "  service pret sur 127.0.0.1:$MinioPort" -ForegroundColor Green

Write-Host "`n== Application ==" -ForegroundColor Cyan

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*Cratebase.App*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep -Seconds 2

# Base locale remise à neuf : les enregistrements doivent repartir de zéro, sinon les assertions
# sur les compteurs échouent pour une raison sans rapport avec le stockage.
Remove-Item -Recurse -Force (Join-Path $root 'src\Cratebase.App\data') -ErrorAction SilentlyContinue

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Cratebase__S3__Bucket = 'cratebase'
$env:Cratebase__S3__AccessKey = 'cratebase'
$env:Cratebase__S3__SecretKey = 'cratebase-secret'
$env:Cratebase__S3__Endpoint = "http://127.0.0.1:$MinioPort"

Start-Process -FilePath 'dotnet' -WindowStyle Hidden -ArgumentList @(
    'run', '--project', (Join-Path $root 'src\Cratebase.App'),
    '--no-launch-profile', '--urls', "http://127.0.0.1:$AppPort"
)

for ($i = 0; $i -lt 90; $i++) {
    try { Invoke-RestMethod "http://127.0.0.1:$AppPort/api/health" -TimeoutSec 3 | Out-Null; break }
    catch { Start-Sleep -Milliseconds 1000 }
}

& (Join-Path $PSScriptRoot 'smoke.ps1') -BaseUrl "http://127.0.0.1:$AppPort"
$outcome = $LASTEXITCODE

# Contrôle décisif : les octets doivent être DANS le seau, et le répertoire local ne doit pas
# exister. Sans lui, un repli silencieux sur le disque ferait passer la suite pour la mauvaise
# raison.
Write-Host "`n== Emplacement reel des octets ==" -ForegroundColor Cyan

$inBucket = (docker exec $Container ls /data/cratebase 2>&1 | Out-String).Trim()
$localPath = Join-Path $root 'src\Cratebase.App\data\storage'

if ($inBucket -and -not (Test-Path $localPath)) {
    Write-Host "  OK   les objets sont dans le seau, rien sur le disque local" -ForegroundColor Green
}
else {
    Write-Host "  ECHEC repli silencieux sur le disque local" -ForegroundColor Red
    $outcome = 1
}

exit $outcome
