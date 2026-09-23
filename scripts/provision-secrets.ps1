<#
.SYNOPSIS
    Puts the dev database connection string, password included, into user-secrets for both
    hosts, reading the password from .env so it is typed nowhere and printed nowhere.

.DESCRIPTION
    appsettings.json carries the connection string without a password on purpose: the file is
    committed. The password lives in .env for compose and in user-secrets for host-side runs.
    User-secrets load only when the environment is Development, which the launch profiles set,
    so run the hosts with `dotnet run --launch-profile http` (Web) or `--launch-profile worker`
    (Worker). A published build runs in Production and reads the connection string from the
    environment instead, the way compose supplies it.

.EXAMPLE
    .\scripts\provision-secrets.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'

if (-not (Test-Path $envFile)) { throw ".env is missing. Run .\scripts\setup.ps1 first." }
$envText = Get-Content $envFile -Raw
if ($envText -notmatch '(?m)^POSTGRES_PASSWORD=(.+)$') { throw ".env has no POSTGRES_PASSWORD." }
$password = $Matches[1].Trim()

$connection = "Host=localhost;Port=5434;Database=ticketmiser;Username=ticketmiser;Password=$password"

foreach ($project in 'src\TicketMiser.Web', 'src\TicketMiser.Worker') {
    $path = Join-Path $root $project
    dotnet user-secrets set 'ConnectionStrings:TicketMiser' $connection --project $path | Out-Null
    Write-Host "Set ConnectionStrings:TicketMiser in user-secrets for $project" -ForegroundColor Green
}

Write-Host ""
Write-Host "Run the hosts in Development so user-secrets load:" -ForegroundColor Cyan
Write-Host "    dotnet run --project src/TicketMiser.Web --launch-profile http" -ForegroundColor White
Write-Host "    dotnet run --project src/TicketMiser.Worker --launch-profile worker" -ForegroundColor White
