<#
.SYNOPSIS
    Forces the Reporting-Service RBA exchange rate sync via its HTTP trigger.

.DESCRIPTION
    Safety net for the in-process sync timer. That timer only runs while the IIS worker
    process is alive; a misconfigured app pool (startMode=OnDemand) left the worker stopped
    and the sync silently dead for four months in 2026 (2026-04-02 -> 2026-08-17).

    This script does not depend on IIS keeping anything alive: the HTTP request itself starts
    the worker process, which then runs the sync. It therefore survives an app-pool config
    regression — a server rebuild, a deploy that resets the pool, or a missed checklist item.

    Normal case: the internal 23:00 UTC timer already ran, so this is a no-op. The service
    skips dates already present in CCURRA rather than overwriting them, so running twice a
    day is harmless by design.

.PARAMETER BaseUrl
    Reporting-Service base URL. Default: http://localhost/reporting

.PARAMETER ApiKey
    X-API-Key value. If omitted, read from the REPORTING_API_KEY environment variable
    (set as a machine-level variable so the scheduled task's service account can see it).

.PARAMETER LogPath
    Rolling log directory. Default: C:\inetpub\wwwroot\Reporting-Api\logs

.PARAMETER TimeoutSec
    HTTP timeout. Default 300 — the sync fetches the RBA CSV and writes up to 8 currencies
    over ODBC, and a cold IIS start adds JIT time on top.

.OUTPUTS
    Exit 0 — sync succeeded, or was legitimately skipped (weekend).
    Exit 1 — sync failed, was disabled, or the service was unreachable.
    Task Scheduler surfaces this as "Last Run Result".

.EXAMPLE
    .\Invoke-ExchangeRateSync.ps1 -Verbose

.NOTES
    Registered as scheduled task: ReportingService-ExchangeRateSync (daily 23:30 UTC / 09:30 AEST)
    See docs/runbooks/TROUBLESHOOTING.md -> IIS Issue 2
#>
[CmdletBinding()]
param(
    [string] $BaseUrl    = 'http://localhost/reporting',
    [string] $ApiKey     = $env:REPORTING_API_KEY,
    [string] $LogPath    = 'C:\inetpub\wwwroot\Reporting-Api\logs',
    [int]    $TimeoutSec = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Logging -----------------------------------------------------------------
if (-not (Test-Path $LogPath)) {
    New-Item -ItemType Directory -Force -Path $LogPath | Out-Null
}
$logFile = Join-Path $LogPath ("exchange-rate-trigger-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))

function Write-Log {
    param([string] $Message, [ValidateSet('INFO','WARN','ERROR')] [string] $Level = 'INFO')
    $line = "{0} [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz'), $Level, $Message
    Add-Content -Path $logFile -Value $line -Encoding utf8
    switch ($Level) {
        'ERROR' { Write-Error   $Message -ErrorAction Continue }
        'WARN'  { Write-Warning $Message }
        default { Write-Verbose $Message -Verbose }
    }
}

# Retain 90 days of trigger logs
Get-ChildItem -Path $LogPath -Filter 'exchange-rate-trigger-*.log' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-90) } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# --- Preconditions -----------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Log "No API key. Pass -ApiKey or set the REPORTING_API_KEY machine environment variable." 'ERROR'
    exit 1
}

$uri = "$($BaseUrl.TrimEnd('/'))/api/v1/exchange-rates/sync"
Write-Log "Triggering exchange rate sync: POST $uri"

# --- Invoke ------------------------------------------------------------------
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $response = Invoke-RestMethod -Method Post -Uri $uri `
        -Headers @{ 'X-API-Key' = $ApiKey } `
        -TimeoutSec $TimeoutSec `
        -ErrorAction Stop
    $sw.Stop()
}
catch {
    $sw.Stop()
    # 503 = disabled or failed; the body still carries the reason, so surface it.
    $detail = $_.Exception.Message
    $resp = $null
    try { $resp = $_.ErrorDetails.Message } catch { }
    if ($resp) { $detail = "$detail | body: $resp" }

    Write-Log "Sync trigger FAILED after $([int]$sw.Elapsed.TotalSeconds)s: $detail" 'ERROR'
    Write-Log "Check: (1) is the app pool running - 'appcmd list wp'; (2) is the API key valid; (3) is ExchangeRateSync:Enabled true" 'ERROR'
    exit 1
}

# --- Interpret result --------------------------------------------------------
# Status values come from SyncStatus: NotRun | Healthy | Degraded | Failed
# plus "Skipped" which the controller substitutes when SkipReason is set (weekend).
$status = $response.status
$elapsed = [int]$sw.Elapsed.TotalSeconds

switch ($status) {
    'Healthy' {
        Write-Log "Sync OK in ${elapsed}s. lastSyncUtc=$($response.lastSyncUtc) next=$($response.nextScheduledSyncUtc)"
        exit 0
    }
    'Skipped' {
        # Weekend — RBA does not publish. Nothing to do; this is a success.
        Write-Log "Sync skipped in ${elapsed}s: $($response.skipReason)"
        exit 0
    }
    'Degraded' {
        # PARTIAL FAILURE: some currencies synced, others errored. Must not pass silently —
        # a missing currency is exactly the gap this whole job exists to prevent.
        Write-Log "Sync DEGRADED in ${elapsed}s - some currencies failed: $($response.lastError)" 'ERROR'
        exit 1
    }
    default {
        Write-Log "Sync returned unexpected status '$status' in ${elapsed}s. error=$($response.lastError)" 'ERROR'
        exit 1
    }
}
