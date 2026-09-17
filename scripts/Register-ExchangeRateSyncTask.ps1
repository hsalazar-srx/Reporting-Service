<#
.SYNOPSIS
    Registers the daily Windows Task Scheduler job that forces the exchange rate sync.

.DESCRIPTION
    Creates (or replaces) the scheduled task 'ReportingService-ExchangeRateSync' on
    SRXWEBAPP1. Runs Invoke-ExchangeRateSync.ps1 daily at 23:30 UTC (09:30 AEST) — 30 minutes
    after the service's own internal timer window, so it acts as a backstop rather than a
    duplicate.

    Run once, elevated, on the server. Re-running replaces the existing task.

.PARAMETER ApiKey
    X-API-Key for Reporting-Service. Stored as a machine-level environment variable
    (REPORTING_API_KEY) so the task's service account can read it. Not embedded in the task
    definition, which is world-readable to local admins via schtasks /query /xml.

.PARAMETER RunAsUser
    Account to run under. Default SYSTEM — the task only makes a localhost HTTP call and
    writes to a local log directory, so it needs no network or domain rights.

.PARAMETER TimeUtc
    Daily run time in UTC. Default 23:30.

.EXAMPLE
    .\Register-ExchangeRateSyncTask.ps1 -ApiKey '<key>'

.NOTES
    Verify:  Get-ScheduledTaskInfo -TaskName 'ReportingService-ExchangeRateSync'
    Run now: Start-ScheduledTask   -TaskName 'ReportingService-ExchangeRateSync'
    Remove:  Unregister-ScheduledTask -TaskName 'ReportingService-ExchangeRateSync' -Confirm:$false
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ApiKey,

    [string] $RunAsUser = 'SYSTEM',
    [string] $TimeUtc   = '23:30'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName   = 'ReportingService-ExchangeRateSync'
$scriptPath = Join-Path $PSScriptRoot 'Invoke-ExchangeRateSync.ps1'

if (-not (Test-Path $scriptPath)) {
    throw "Cannot find $scriptPath - run this from the scripts folder of the deployed service."
}

# --- Elevation check ---------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Must run elevated - task registration and machine env vars require Administrator."
}

# --- Store the API key as a machine env var (not in the task definition) -----
Write-Host "Setting machine environment variable REPORTING_API_KEY..."
[Environment]::SetEnvironmentVariable('REPORTING_API_KEY', $ApiKey, 'Machine')

# --- Convert the UTC run time to local, since Task Scheduler triggers are local ---
$parsed     = [datetime]::ParseExact($TimeUtc, 'HH:mm', $null)
$utcToday   = [datetime]::SpecifyKind(
                  (Get-Date -Hour $parsed.Hour -Minute $parsed.Minute -Second 0),
                  [DateTimeKind]::Utc)
$localTime  = $utcToday.ToLocalTime()

Write-Host ("Schedule: {0} UTC  ->  {1} local ({2})" -f `
    $TimeUtc, $localTime.ToString('HH:mm'), [TimeZoneInfo]::Local.Id)

# --- Build the task ----------------------------------------------------------
$action = New-ScheduledTaskAction `
    -Execute  'powershell.exe' `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$scriptPath`"" `
    -WorkingDirectory $PSScriptRoot

$trigger = New-ScheduledTaskTrigger -Daily -At $localTime

$principal = New-ScheduledTaskPrincipal `
    -UserId $RunAsUser -LogonType ServiceAccount -RunLevel Highest

# StartWhenAvailable: if the server was off at 23:30, run at next boot rather than skip a day.
# Retry 3x/10min: covers a transient RBA outage or a slow cold IIS start.
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopOnIdleEnd `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 10) `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 15) `
    -MultipleInstances IgnoreNew

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Write-Host "Existing task found - replacing."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName    $taskName `
    -Action      $action `
    -Trigger     $trigger `
    -Principal   $principal `
    -Settings    $settings `
    -Description ('Daily backstop for the Reporting-Service RBA exchange rate sync. ' +
                  'The in-process timer only runs while the IIS worker is alive; this HTTP ' +
                  'trigger starts the worker if it is stopped. See TROUBLESHOOTING.md IIS Issue 2.') `
    | Out-Null

Write-Host "`nRegistered '$taskName'."
Write-Host "`nVerify with a live run:"
Write-Host "  Start-ScheduledTask -TaskName '$taskName'"
Write-Host "  Start-Sleep 30"
Write-Host "  Get-ScheduledTaskInfo -TaskName '$taskName' | Select LastRunTime, LastTaskResult"
Write-Host "`n  LastTaskResult 0 = success. Log: C:\inetpub\wwwroot\Reporting-Api\logs\exchange-rate-trigger-*.log"
