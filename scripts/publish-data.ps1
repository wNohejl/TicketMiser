<#
.SYNOPSIS
    Snapshot the local TicketMiser database into the repository, so another machine can
    restore exactly the same data with one command.

.DESCRIPTION
    The desk's data lives in a Docker Postgres on this machine. The database is published as
    a compressed pg_dump under data/snapshots/ and committed alongside the code. The on-sale
    record is in it, always: those rows are never pruned and are the point of the product.
    Personal data is not: the Subscriptions, AlertDeliveries, ReportSubscriptions,
    ReportDeliveries, Accounts and SignInTokens tables are dumped without their rows, and
    the watches and purchases an account owns are left out with them. The operator's own (unowned) watches and purchases travel as before.

    pg_dump can leave out a table's rows but not some of them, and an owned watch or purchase
    points at an Accounts row by foreign key, so a snapshot with Accounts empty and owned rows
    present would not even restore. The dump is therefore taken from a scratch copy inside
    the container, from which the personal rows have been deleted, and the scratch copy is
    dropped afterwards. Nothing personal is written outside the container.

.PARAMETER OutDir
    Where to write the snapshot and its manifest. Defaults to data/snapshots, the committed
    location; point it elsewhere to inspect a snapshot without touching the committed one.

.PARAMETER Commit
    Also commit the snapshot (your git identity, no trailer) and push to origin.

.EXAMPLE
    .\scripts\publish-data.ps1 -Commit
#>
param(
    [string]$Container = "ticketmiser-postgres",
    [string]$Database = "ticketmiser",
    [string]$User = "ticketmiser",
    [string]$OutDir,
    [switch]$Commit
)

$ErrorActionPreference = "Stop"
function Invoke-Native([scriptblock]$Block) { $ErrorActionPreference = "Continue"; & $Block }
$root = Split-Path $PSScriptRoot -Parent
$dir = if ($OutDir) { $OutDir } else { Join-Path $root "data\snapshots" }
if ($OutDir -and $Commit) { throw "-Commit publishes the committed snapshot; it does not combine with -OutDir." }
$dump = Join-Path $dir "ticketmiser.dump"
$manifest = Join-Path $dir "ticketmiser.dump.json"

New-Item -ItemType Directory -Force $dir | Out-Null

Invoke-Native { docker inspect $Container *> $null }
if ($LASTEXITCODE -ne 0) { throw "Container '$Container' is not running. Start it: docker compose -f docker-compose.yml -f compose.dev.yml up -d postgres" }
$envText = Get-Content (Join-Path $root ".env") -Raw
if ($envText -notmatch '(?m)^POSTGRES_PASSWORD=(.+)$') { throw ".env has no POSTGRES_PASSWORD. Run .\scripts\setup.ps1 first." }
$pgEnv = "PGPASSWORD=$($Matches[1].Trim())"

# Subscriber addresses (the event alerts' and the monthly report's), the record of what was sent
# to them, accounts and their sign-in links are personal data, and a snapshot leaves the machine
# (legal-guidelines rule 8). Their tables travel as schema only, so a restore has them empty.
# The \" survives cmd and reaches pg_dump as a quoted, case-sensitive table name.
$personalTables = @("Subscriptions", "AlertDeliveries", "ReportSubscriptions", "ReportDeliveries", "Accounts", "SignInTokens")
$excludeData = $personalTables | ForEach-Object { "--exclude-table-data=public.\`"$_\`"" }

# The scratch copy: every row but the sign-in links and both lists' subscriptions and deliveries
# (Accounts stays for the moment, so the owned rows that point at it restore), then the owned
# rows and the accounts deleted (and both lists again, should a row have got through), then
# the published dump taken from what is left.
$scratch = "$($Database)_publish"
$scratchFile = "/tmp/$scratch.dump"
$scratchSql = @"
DELETE FROM "Watches" WHERE "OwnerId" IS NOT NULL;
DELETE FROM "Purchases" WHERE "OwnerId" IS NOT NULL;
DELETE FROM "Subscriptions";
DELETE FROM "ReportSubscriptions";
DELETE FROM "SignInTokens";
DELETE FROM "Accounts";
"@
function Invoke-Psql([string]$Db, [string]$Sql) {
    $out = Invoke-Native { $Sql | docker exec -i -e $pgEnv $Container psql -U $User -d $Db -X -q -v ON_ERROR_STOP=1 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "psql on $Db failed: $out" }
}
try {
    $excludeFirst = @("Subscriptions", "AlertDeliveries", "ReportSubscriptions", "ReportDeliveries", "SignInTokens") | ForEach-Object { "--exclude-table-data=public.\`"$_\`"" }
    Invoke-Native { cmd /c "docker exec -e $pgEnv $Container pg_dump -U $User -Fc -Z 1 $($excludeFirst -join ' ') -f $scratchFile $Database" }
    if ($LASTEXITCODE -ne 0) { throw "pg_dump of the working copy failed" }

    Invoke-Psql "postgres" "DROP DATABASE IF EXISTS `"$scratch`"; CREATE DATABASE `"$scratch`";"
    Invoke-Native { docker exec -e $pgEnv $Container pg_restore -U $User -d $scratch --no-owner --no-privileges $scratchFile }
    if ($LASTEXITCODE -ne 0) { throw "pg_restore into the scratch copy failed" }
    Invoke-Psql $scratch $scratchSql

    Invoke-Native { cmd /c "docker exec -e $pgEnv $Container pg_dump -U $User -Fc -Z 6 $($excludeData -join ' ') $scratch > `"$dump`"" }
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
    # Counted from the scratch copy, so the manifest describes the snapshot and not the machine.
    $counts = (Invoke-Native { $sql | docker exec -i -e $pgEnv $Container psql -U $User -d $scratch -X -q -A -t }) -join ""
    if (-not $counts) { throw "manifest query returned nothing" }
    [System.IO.File]::WriteAllText($manifest, $counts.Trim() + "`n")
}
finally {
    Invoke-Native { "DROP DATABASE IF EXISTS `"$scratch`";" | docker exec -i -e $pgEnv $Container psql -U $User -d postgres -X -q 2>&1 | Out-Null }
    Invoke-Native { docker exec $Container rm -f $scratchFile 2>&1 | Out-Null }
}

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
