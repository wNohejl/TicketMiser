<#
.SYNOPSIS
  The pre-deploy gate: build, format, test, secret scan, migrations, container. Stops at the first failure.
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [switch] $SkipContainer
)

$ErrorActionPreference = 'Stop'
Set-Location $Root
$failed = $false
$skipped = @()

function Step([string] $Name, [scriptblock] $Body) {
    if ($script:failed) { return }
    Write-Host ""
    Write-Host "== $Name" -ForegroundColor Cyan
    try {
        $result = & $Body
        if ($result -eq 'SKIP') { $script:skipped += $Name; Write-Host "   skipped" -ForegroundColor Yellow; return }
        if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
        Write-Host "   pass" -ForegroundColor Green
    } catch {
        $script:failed = $true
        Write-Host "   FAIL: $_" -ForegroundColor Red
    }
}

Step 'dotnet build (Release)' { dotnet build TicketMiser.slnx -c Release -nologo -v q -p:TreatWarningsAsErrors=true | Out-Host }

Step 'dotnet format --verify-no-changes' { dotnet format TicketMiser.slnx --verify-no-changes --no-restore | Out-Host }

Step 'dotnet test (Release, no build)' { dotnet test TicketMiser.slnx -c Release --no-build -nologo -v q | Out-Host }

Step 'secret scan of tracked files' {
    $global:LASTEXITCODE = 0
    $tracked = git ls-files
    if ($tracked -contains '.env') { throw ".env is tracked" }
    $patterns = @(
        '(?i)apikey=[A-Za-z0-9]{16,}',
        '(?i)client_(id|secret)=[A-Za-z0-9]{16,}',
        '(?i)(password|pwd)\s*[:=]\s*"[^"$<{][^"]{7,}"',
        '-----BEGIN (RSA |EC )?PRIVATE KEY-----',
        '(?i)TICKETMISER_[A-Z_]+_(KEY|CLIENT_ID)\s*=\s*[A-Za-z0-9]{8,}'
    )
    $hits = @()
    foreach ($f in $tracked) {
        if ($f -match '\.(png|jpg|gif|ico|woff2?|dump|pfx)$') { continue }
        $text = Get-Content -Raw -ErrorAction SilentlyContinue $f
        if (-not $text) { continue }
        foreach ($p in $patterns) { if ($text -match $p) { $hits += "$f : $p" } }
    }
    if ($hits) { throw ("secret-shaped content in: " + ($hits -join '; ')) }
}

Step 'ef migrations has-pending-model-changes' {
    $data = Get-ChildItem -Directory src | Where-Object Name -like '*.Data' | Select-Object -First 1
    if (-not $data) { return 'SKIP' }
    dotnet ef migrations has-pending-model-changes --project $data.FullName --no-build | Out-Host
}

Step 'container build and health' {
    if ($SkipContainer -or -not (Test-Path 'docker-compose.yml')) { return 'SKIP' }

    # Docker writes its progress to stderr. Under Windows PowerShell 5.1 with
    # ErrorActionPreference Stop, any stderr line from a native command is a terminating
    # error, so the build "failed" on its first progress line. Judge docker by its exit code.
    function Invoke-Docker([string[]] $Arguments) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & docker @Arguments 2>&1 | ForEach-Object { "$_" } | Out-Host }
        finally { $ErrorActionPreference = $previous }
        if ($LASTEXITCODE -ne 0) { throw "docker $($Arguments -join ' ') exited $LASTEXITCODE" }
    }

    Invoke-Docker @('compose', 'config', '-q')
    Invoke-Docker @('compose', 'build')
    Invoke-Docker @('compose', 'up', '-d')
    $ok = $false
    try {
        # curl.exe ships with Windows 10 and later; -k because the development certificate is
        # self-signed. Invoke-WebRequest -SkipCertificateCheck exists only in PowerShell 7.
        for ($i = 0; $i -lt 30; $i++) {
            $code = & curl.exe -k -s -o NUL -w '%{http_code}' --max-time 3 'https://localhost:8443/health'
            if ($code -eq '200') { $ok = $true; break }
            Start-Sleep -Seconds 2
        }
    }
    finally {
        Invoke-Docker @('compose', 'down')
    }
    if (-not $ok) { throw "/health did not answer 200 within 60 s" }
    $global:LASTEXITCODE = 0
}

Step 'discovery feed size cap configured' {
    $cfg = Get-ChildItem -Recurse -Filter appsettings.json src -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw $_.FullName }
    if (-not ($cfg -match 'DiscoveryFeed')) { return 'SKIP' }
    if (-not ($cfg -match 'MaxBytes')) { throw "Ingestion:DiscoveryFeed:MaxBytes is not set" }
}

Write-Host ""
if ($failed) { Write-Host "RELEASE GATE: FAILED" -ForegroundColor Red; exit 1 }
if ($skipped) { Write-Host ("RELEASE GATE: passed with skipped steps: " + ($skipped -join ', ')) -ForegroundColor Yellow; exit 0 }
Write-Host "RELEASE GATE: PASSED" -ForegroundColor Green
