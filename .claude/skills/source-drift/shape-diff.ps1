<#
.SYNOPSIS
  Compare the shape (paths and types) of a live JSON response with a saved fixture.

.EXAMPLE
  .\shape-diff.ps1 -Fixture tests/TicketMiser.Tests/Fixtures/seatgeek/ryman-stats.json -Live C:\tmp\probe.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Fixture,
    [Parameter(Mandatory)] [string] $Live
)

$ErrorActionPreference = 'Stop'

function Flatten($node, [string] $path, [hashtable] $acc) {
    if ($null -eq $node) { $acc[$path] = 'null'; return }
    if ($node -is [System.Collections.IList]) {
        $acc[$path] = 'array'
        foreach ($item in $node) { Flatten $item "$path[]" $acc }
        return
    }
    if ($node -is [System.Management.Automation.PSCustomObject]) {
        $acc[$path] = 'object'
        foreach ($p in $node.PSObject.Properties) {
            $child = if ($path) { "$path.$($p.Name)" } else { $p.Name }
            Flatten $p.Value $child $acc
        }
        return
    }
    $type = switch ($node.GetType().Name) {
        'String'  { 'string' }
        'Boolean' { 'boolean' }
        default   { 'number' }
    }
    # A path seen with two scalar types (e.g. null then number) is recorded as the non-null one.
    if (-not $acc.ContainsKey($path) -or $acc[$path] -eq 'null') { $acc[$path] = $type }
}

$a = @{}; $b = @{}
Flatten (Get-Content -Raw $Fixture | ConvertFrom-Json) '' $a
Flatten (Get-Content -Raw $Live | ConvertFrom-Json) '' $b

$onlyLive = $b.Keys | Where-Object { -not $a.ContainsKey($_) } | Sort-Object
$onlyFixture = $a.Keys | Where-Object { -not $b.ContainsKey($_) } | Sort-Object
$retyped = $a.Keys | Where-Object { $b.ContainsKey($_) -and $a[$_] -ne $b[$_] -and $a[$_] -ne 'null' -and $b[$_] -ne 'null' } | Sort-Object

Write-Host ""
Write-Host "Only in live ($($onlyLive.Count)):" -ForegroundColor Cyan
$onlyLive | ForEach-Object { Write-Host "  + $_ : $($b[$_])" }
Write-Host "Only in fixture ($($onlyFixture.Count)):" -ForegroundColor Cyan
$onlyFixture | ForEach-Object { Write-Host "  - $_ : $($a[$_])" -ForegroundColor Red }
Write-Host "Type changed ($($retyped.Count)):" -ForegroundColor Cyan
$retyped | ForEach-Object { Write-Host "  ~ $_ : $($a[$_]) -> $($b[$_])" -ForegroundColor Yellow }
Write-Host ""

if ($onlyLive.Count -or $onlyFixture.Count -or $retyped.Count) { exit 1 }
Write-Host "No drift." -ForegroundColor Green
