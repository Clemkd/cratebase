# Replays the end-to-end suite with S3 (MinIO) object storage.
#
# Counterpart to `postgres.ps1` for the other half of the scalability promise: moving from local
# disk to S3 must stay a configuration change. The day this script breaks, that's no longer true.
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
Write-Host "  service ready on 127.0.0.1:$MinioPort" -ForegroundColor Green

Write-Host "`n== Application ==" -ForegroundColor Cyan

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*Cratebase.App*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep -Seconds 2

# Local database reset from scratch: records must start from zero, otherwise the assertions on
# counters fail for a reason unrelated to storage.
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

# Decisive check: the bytes must be IN the bucket, and the local directory must not exist. Without
# this, a silent fallback to disk would make the suite pass for the wrong reason.
Write-Host "`n== Actual byte location ==" -ForegroundColor Cyan

$inBucket = (docker exec $Container ls /data/cratebase 2>&1 | Out-String).Trim()
$localPath = Join-Path $root 'src\Cratebase.App\data\storage'

if ($inBucket -and -not (Test-Path $localPath)) {
    Write-Host "  OK   objects are in the bucket, nothing on local disk" -ForegroundColor Green
}
else {
    Write-Host "  FAIL silent fallback to local disk" -ForegroundColor Red
    $outcome = 1
}

exit $outcome
