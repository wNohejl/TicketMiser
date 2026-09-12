<#
.SYNOPSIS
    Snapshot the local TicketMiser database into the repository, so another machine can
    restore exactly the same data with one command.

.DESCRIPTION
    The desk's data lives in a Docker Postgres on this machine. The database is published as
    a compressed pg_dump under data/snapshots/ and committed alongside the code. The on-sale
    record is in it, always: those rows are never pruned and are the point of the product.

.PARAMETER Commit
    Also commit the snapshot (your git identity, no trailer) and push to origin.

.EXAMPLE
    .\scripts\publish-data.ps1 -Commit
#>
param(
    [string]$Container = "ticketmiser-postgres",
    [string]$Database = "ticketmiser",
    [string]$User = "ticketmiser",
    [switch]$Commit
)

$ErrorActionPreference = "Stop"
function Invoke-Native([scriptblock]$Block) { $ErrorActionPreference = "Continue"; & $Block }
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root "data\snapshots"
$dump = Join-Path $dir "ticketmiser.dump"
$manifest = Join-Path $dir "ticketmiser.dump.json"

New-Item -ItemType Directory -Force $dir | Out-Null

Invoke-Native { docker inspect $Container *> $null }
if ($LASTEXITCODE -ne 0) { throw "Container '$Container' is not running. Start it: docker compose -f docker-compose.yml -f compose.dev.yml up -d postgres" }
$envText = Get-Content (Join-Path $root ".env") -Raw
if ($envText -notmatch '(?m)^POSTGRES_PASSWORD=(.+)$') { throw ".env has no POSTGRES_PASSWORD. Run .\scripts\setup.ps1 first." }
$pgEnv = "PGPASSWORD=$($Matches[1].Trim())"

Invoke-Native { cmd /c "docker exec -e $pgEnv $Container pg_dump -U $User -Fc -Z 6 $Database > `"$dump`"" }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dump) -or (Get-Item $dump).Length -lt 1024) { throw "pg_dump failed" }

$sql = @"
select json_build_object(
  'takenAtUtc', to_char(now() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
  'events', (select count(*) from "Events"),
  'watches', (select count(*) from "Watches" where "Enabled"),
  'observations', (select count(*) from "PriceObservations"),
  'onSaleTicks', (select count(*) from "OnSaleTicks"),
  'finalPrices', (select count(*) from "FinalPrices"),
  'purchases', (select count(*) from "Purchases")
)::text;
"@
$counts = (Invoke-Native { $sql | docker exec -i -e $pgEnv $Container psql -U $User -d $Database -X -q -A -t }) -join ""
if (-not $counts) { throw "manifest query returned nothing" }
[System.IO.File]::WriteAllText($manifest, $counts.Trim() + "`n")

$size = "{0:N1} MB" -f ((Get-Item $dump).Length / 1MB)
Write-Host "Snapshot written: $dump ($size)"
Write-Host $counts

if ($Commit) {
    Push-Location $root
    try {
        git add -- $dump $manifest
        $date = Get-Date -Format "yyyy-MM-dd"
        git commit -m "data: snapshot $date" -- $dump $manifest
        git push
    }
    finally { Pop-Location }
}
