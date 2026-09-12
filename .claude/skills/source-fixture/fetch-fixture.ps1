<#
.SYNOPSIS
  Fetch one real response from a ticket source and save it as a committed test fixture.

.DESCRIPTION
  Adds the source's key from the environment, fetches, redacts the key from the body,
  pretty-prints JSON, and writes <name>.json and <name>.meta.json under
  tests/TicketMiser.Tests/Fixtures/<source>/. Refuses to write a body that still contains
  the key. Keys never enter the repository.

.EXAMPLE
  .\fetch-fixture.ps1 -Source seatgeek -Name ryman-stats -Url "https://api.seatgeek.com/2/events?venue.city=Nashville&per_page=5"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('ticketmaster', 'inventory-status', 'discovery-feed', 'seatgeek')] [string] $Source,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9]+(-[a-z0-9]+)*$')] [string] $Name,
    [Parameter(Mandatory)] [string] $Url,
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
)

$ErrorActionPreference = 'Stop'

$keyVar, $keyParam = switch ($Source) {
    'ticketmaster'     { 'TICKETMISER_TICKETMASTER_KEY', 'apikey' }
    'inventory-status' { 'TICKETMISER_INVENTORY_KEY', 'apikey' }
    'discovery-feed'   { 'TICKETMISER_TICKETMASTER_KEY', 'apikey' }
    'seatgeek'         { 'TICKETMISER_SEATGEEK_CLIENT_ID', 'client_id' }
}

$key = [Environment]::GetEnvironmentVariable($keyVar)
if ([string]::IsNullOrWhiteSpace($key)) {
    throw "Set $keyVar in the environment first. Keys are never stored in the repository."
}
if ($Url -match "(?i)(apikey|client_id|client_secret)=") {
    throw "Pass the URL without the key; the script adds it."
}

$separator = if ($Url.Contains('?')) { '&' } else { '?' }
$requestUrl = "$Url$separator$keyParam=$([Uri]::EscapeDataString($key))"

$headers = @{ 'User-Agent' = 'TicketMiser/0.1 (+https://github.com/wNohejl/TicketMiser)' }
$fetchedAt = (Get-Date).ToUniversalTime().ToString('o')

$response = Invoke-WebRequest -Uri $requestUrl -Headers $headers -UseBasicParsing
$body = $response.Content

if ($Source -eq 'discovery-feed' -and $response.Headers['Content-Encoding'] -eq 'gzip') {
    throw "The feed is a gzipped file; download it with Invoke-WebRequest -OutFile and slice a Tennessee sample by hand before saving a fixture."
}

# Redact the key wherever the body echoes it, then refuse to save if it is still there.
$redacted = $body.Replace($key, '<REDACTED>').Replace([Uri]::EscapeDataString($key), '<REDACTED>')
if ($redacted.Contains($key)) { throw "Key still present in body after redaction; not saving." }

# Pretty-print JSON so diffs read line by line.
try {
    $pretty = ($redacted | ConvertFrom-Json) | ConvertTo-Json -Depth 64
} catch {
    $pretty = $redacted
}

$dir = Join-Path $Root "tests\TicketMiser.Tests\Fixtures\$Source"
New-Item -ItemType Directory -Force $dir | Out-Null

$fixturePath = Join-Path $dir "$Name.json"
$metaPath = Join-Path $dir "$Name.meta.json"

[IO.File]::WriteAllText($fixturePath, $pretty + "`n", [Text.UTF8Encoding]::new($false))

$meta = [ordered]@{
    source     = $Source
    name       = $Name
    url        = $Url
    fetchedAt  = $fetchedAt
    status     = [int]$response.StatusCode
    bytes      = [Text.Encoding]::UTF8.GetByteCount($body)
    redactions = @("$keyParam")
    note       = 'Add what else was removed, and why, before committing.'
}
[IO.File]::WriteAllText($metaPath, (($meta | ConvertTo-Json -Depth 4) + "`n"), [Text.UTF8Encoding]::new($false))

Write-Host "Saved $fixturePath ($($meta.bytes) bytes, HTTP $($meta.status))"
Write-Host "Meta  $metaPath"
