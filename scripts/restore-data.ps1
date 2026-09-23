<#
.SYNOPSIS
    Restore the committed database snapshot into the local Docker Postgres.

.DESCRIPTION
    Starts the dev Postgres if it is not running, waits for it to be healthy, then drops and
    recreates the public schema from data/snapshots/ticketmiser.dump. Refuses to overwrite a
    database that already holds events unless -Force is given, because a restore is not a
    merge.

.EXAMPLE
    .\scripts\restore-data.ps1            # first time on a laptop
    .\scripts\restore-data.ps1 -Force     # replace local data with the latest snapshot
#>
param(
    [string]$Container = "ticketmiser-postgres",
    [string]$Database = "ticketmiser",
    [string]$User = "ticketmiser",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
function Invoke-Native([scriptblock]$Block) { $ErrorActionPreference = "Continue"; & $Block }
$root = Split-Path $PSScriptRoot -Parent
$dump = Join-Path $root "data\snapshots\ticketmiser.dump"

if (-not (Test-Path $dump)) { throw "No snapshot at $dump. Run scripts/publish-data.ps1 on the machine that has the data." }
if (-not (Test-Path (Join-Path $root ".env"))) { throw ".env is missing. Run .\scripts\setup.ps1 first." }
$envText = Get-Content (Join-Path $root ".env") -Raw
if ($envText -notmatch '(?m)^POSTGRES_PASSWORD=(.+)$') { throw ".env has no POSTGRES_PASSWORD. Run .\scripts\setup.ps1 first." }
$pgEnv = "PGPASSWORD=$($Matches[1].Trim())"

Push-Location $root
try {
    Invoke-Native { docker compose -f docker-compose.yml -f compose.dev.yml up -d postgres 2>&1 | Out-Null }
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed to start postgres (exit $LASTEXITCODE)" }

    $deadline = (Get-Date).AddSeconds(90)
    do {
        $status = Invoke-Native { docker inspect --format '{{.State.Health.Status}}' $Container 2>$null }
        if ($status -eq "healthy") { break }
        Start-Sleep 3
    } while ((Get-Date) -lt $deadline)
    if ($status -ne "healthy") { throw "Postgres did not become healthy in time (status: $status)" }

    $existing = Invoke-Native { 'select count(*) from "Events"' | docker exec -i -e $pgEnv $Container psql -U $User -d $Database -X -q -A -t 2>$null }
    if ($existing -and [int]$existing.Trim() -gt 0 -and -not $Force) {
        throw "This database already holds $($existing.Trim()) events. Re-run with -Force to replace them with the snapshot."
    }

    Invoke-Native { 'set client_min_messages = warning; drop schema public cascade; create schema public;' | docker exec -i -e $pgEnv $Container psql -U $User -d $Database -X -q -v ON_ERROR_STOP=1 }
    if ($LASTEXITCODE -ne 0) { throw "could not reset the public schema (exit $LASTEXITCODE)" }

    Invoke-Native { cmd /c "docker exec -i -e $pgEnv $Container pg_restore -U $User -d $Database --no-owner --no-privileges < `"$dump`"" }
    if ($LASTEXITCODE -ne 0) { throw "pg_restore failed (exit $LASTEXITCODE)" }

    $counts = Invoke-Native { 'select ''events: '' || (select count(*) from "Events") || '', ticks: '' || (select count(*) from "OnSaleTicks")' | docker exec -i -e $pgEnv $Container psql -U $User -d $Database -X -q -A -t }
    Write-Host "Restored. $counts"
    Write-Host "Now: dotnet run --project src/TicketMiser.Web --launch-profile http"
}
finally { Pop-Location }
