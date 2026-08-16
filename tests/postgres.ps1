# Replays the end-to-end suite against PostgreSQL.
#
# This is THE test that stands as a promise: the same suite, the same application, the other
# engine. As long as it passes, "switching to PostgreSQL" stays a configuration change. The day
# it breaks, the promise of §1 of the design document has stopped being true — well before any
# user notices it in production.
#
#   pwsh tests/postgres.ps1

param(
    [int]$PostgresPort = 61432,
    [int]$AppPort = 8090,
    [string]$Container = 'cratebase-pg'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path $PSScriptRoot -Parent

Write-Host "== PostgreSQL ==" -ForegroundColor Cyan

if (-not (docker ps -a --filter "name=^$Container$" --format '{{.Names}}')) {
    docker run -d --name $Container -p "${PostgresPort}:5432" `
        -e 'POSTGRES_DB=cratebase' -e 'POSTGRES_USER=cratebase' -e 'POSTGRES_PASSWORD=cratebase' `
        'postgres:18-alpine' | Out-Null
}
else {
    docker start $Container | Out-Null
}

for ($i = 0; $i -lt 60; $i++) {
    if (((docker exec $Container pg_isready -U cratebase -d cratebase) 2>&1 | Out-String) -match 'accepting connections') {
        break
    }
    Start-Sleep -Milliseconds 1000
}

# Fresh database on every run: a leftover schema would mask a DDL regression.
docker exec $Container psql -U cratebase -d cratebase -c 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;' | Out-Null
Write-Host "  database reset" -ForegroundColor Green

Write-Host "`n== Application ==" -ForegroundColor Cyan

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*Cratebase.App*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep -Seconds 2

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Postgres =
    "Host=127.0.0.1;Port=$PostgresPort;Database=cratebase;Username=cratebase;Password=cratebase"

# 127.0.0.1, not "localhost": on a machine where localhost resolves to ::1 first, an application
# bound to IPv4 only looks unreachable until the request times out.
Start-Process -FilePath 'dotnet' -WindowStyle Hidden -ArgumentList @(
    'run', '--project', (Join-Path $root 'src\Cratebase.App'),
    '--no-launch-profile', '--urls', "http://127.0.0.1:$AppPort"
)

$health = $null
for ($i = 0; $i -lt 90; $i++) {
    try { $health = Invoke-RestMethod "http://127.0.0.1:$AppPort/api/health" -TimeoutSec 3; break }
    catch { Start-Sleep -Milliseconds 1000 }
}

if (-not $health) { throw "The application did not start." }
if ($health.engine -ne 'postgres') { throw "Unexpected engine: $($health.engine)" }

Write-Host "  engine confirmed: $($health.engine)" -ForegroundColor Green

& (Join-Path $PSScriptRoot 'smoke.ps1') -BaseUrl "http://127.0.0.1:$AppPort"
exit $LASTEXITCODE
