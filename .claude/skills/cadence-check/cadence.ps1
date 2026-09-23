<#
.SYNOPSIS
  Print the day's polling plan for each ticket source from the watchlist size and the limits.

.EXAMPLE
  .\cadence.ps1 -Watchlist 100 -OnSaleEvents 3
#>
[CmdletBinding()]
param(
    # Distinct watched events across every owner (the union the scheduler polls), not watch
    # rows: two accounts watching one event are one call a sweep. SKILL.md has the query.
    [int] $Watchlist = 40,
    [int] $OnSaleEvents = 0,
    [int] $HoursLeft = 24,
    [double] $ReservePercent = 20,

    # Ticketmaster Discovery: published 5,000/day and 5/s. Price ranges refresh at most hourly.
    [int] $TicketmasterDaily = 5000,
    [int] $TicketmasterPerSecond = 5,
    [int] $TicketmasterRefreshMinutes = 60,

    # Ticketmaster Inventory Status: separate key, ~350 ids per call, limit not published.
    [int] $InventoryIdsPerCall = 350,

    # SeatGeek: no published limit; this is our own ceiling.
    [int] $SeatGeekPerHour = 600,

    # On-sale watch: every 5 minutes from T-15m to T+2h (27 ticks), then hourly for 24h.
    [int] $OnSaleTicks = 27 + 13
)

$ErrorActionPreference = 'Stop'
if ($ReservePercent -lt 20) { throw "Reserve below 20% is not allowed (CLAUDE.md)." }

function Plan {
    param([string] $Source, [int] $Daily, [int] $PerSecond, [int] $RefreshMinutes, [int] $OnSaleCost)
    $reserve = [math]::Ceiling($Daily * $ReservePercent / 100)
    $available = $Daily - $reserve
    $forSweeps = $available - $OnSaleCost
    $callsPerSweep = [math]::Max($Watchlist, 1)
    $sweeps = if ($forSweeps -gt 0) { [math]::Floor($forSweeps / $callsPerSweep) } else { 0 }
    $intervalMin = if ($sweeps -gt 0) { [math]::Round($HoursLeft * 60 / $sweeps, 1) } else { [double]::PositiveInfinity }
    $burstSeconds = if ($PerSecond -gt 0) { [math]::Ceiling($callsPerSweep / $PerSecond) } else { 0 }

    [pscustomobject]@{
        Source              = $Source
        DailyLimit          = $Daily
        Reserve             = $reserve
        OnSaleWatchCost     = $OnSaleCost
        LeftForSweeps       = $forSweeps
        CallsPerSweep       = $callsPerSweep
        SweepsPerDay        = $sweeps
        IntervalMinutes     = $intervalMin
        SweepBurstSeconds   = $burstSeconds
        FasterThanRefresh   = ($RefreshMinutes -gt 0 -and $intervalMin -lt $RefreshMinutes)
        Sane                = ($sweeps -ge $HoursLeft) -and ($forSweeps -gt 0)
    }
}

$tmOnSale = $OnSaleEvents * $OnSaleTicks
$sgOnSale = $OnSaleEvents * $OnSaleTicks
$inventoryCalls = if ($OnSaleEvents -gt 0) { $OnSaleTicks * [math]::Ceiling($Watchlist / $InventoryIdsPerCall) } else { 0 }

$plans = @(
    (Plan -Source 'ticketmaster' -Daily $TicketmasterDaily -PerSecond $TicketmasterPerSecond -RefreshMinutes $TicketmasterRefreshMinutes -OnSaleCost $tmOnSale),
    (Plan -Source 'seatgeek' -Daily ($SeatGeekPerHour * 24) -PerSecond 0 -RefreshMinutes 0 -OnSaleCost $sgOnSale)
)

Write-Host ""
Write-Host "Polling plan - watchlist $Watchlist, $OnSaleEvents on-sale event(s) today, $HoursLeft h left, reserve $ReservePercent%"
Write-Host ""
$plans | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Inventory Status calls for the on-sale windows (whole watchlist per tick): $inventoryCalls"
Write-Host ""

foreach ($p in $plans) {
    if (-not $p.Sane) {
        Write-Host "NOT SANE: $($p.Source) cannot sweep hourly with $OnSaleEvents on-sale watches. Reduce the watchlist or the on-sale count." -ForegroundColor Red
    } elseif ($p.FasterThanRefresh) {
        Write-Host "NOTE: $($p.Source) sweeps every $($p.IntervalMinutes) min but the source refreshes prices every $TicketmasterRefreshMinutes min; extra sweeps buy status flips only." -ForegroundColor Yellow
    } else {
        Write-Host "OK: $($p.Source) sweeps every $($p.IntervalMinutes) min." -ForegroundColor Green
    }
}

if ($plans | Where-Object { -not $_.Sane }) { exit 1 }
