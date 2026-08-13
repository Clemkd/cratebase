# Rejoue la suite de bout en bout sur PostgreSQL.
#
# C'est LE test qui vaut promesse : la même suite, la même application, l'autre moteur. Tant qu'il
# passe, « passer à PostgreSQL » reste un changement de configuration. Le jour où il casse, la
# promesse du §1 du document de conception a cessé d'être vraie — bien avant qu'un utilisateur s'en
# aperçoive en production.
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

# Base vierge à chaque exécution : un schéma résiduel masquerait une régression du DDL.
docker exec $Container psql -U cratebase -d cratebase -c 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;' | Out-Null
Write-Host "  base remise a neuf" -ForegroundColor Green

Write-Host "`n== Application ==" -ForegroundColor Cyan

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*Cratebase.App*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep -Seconds 2

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Postgres =
    "Host=127.0.0.1;Port=$PostgresPort;Database=cratebase;Username=cratebase;Password=cratebase"

# 127.0.0.1 et non « localhost » : sur un poste où localhost se résout d'abord en ::1, une
# application liée en IPv4 seule paraît injoignable jusqu'au délai d'expiration.
Start-Process -FilePath 'dotnet' -WindowStyle Hidden -ArgumentList @(
    'run', '--project', (Join-Path $root 'src\Cratebase.App'),
    '--no-launch-profile', '--urls', "http://127.0.0.1:$AppPort"
)

$health = $null
for ($i = 0; $i -lt 90; $i++) {
    try { $health = Invoke-RestMethod "http://127.0.0.1:$AppPort/api/health" -TimeoutSec 3; break }
    catch { Start-Sleep -Milliseconds 1000 }
}

if (-not $health) { throw "L'application n'a pas demarre." }
if ($health.engine -ne 'postgres') { throw "Moteur inattendu : $($health.engine)" }

Write-Host "  moteur confirme : $($health.engine)" -ForegroundColor Green

& (Join-Path $PSScriptRoot 'smoke.ps1') -BaseUrl "http://127.0.0.1:$AppPort"
exit $LASTEXITCODE
