# UAT Deployment Runbook — Reporting Service

| Field | Value |
|---|---|
| **Service** | Reporting Service (`Reporting.Api`) |
| **Server** | SRXWEBAPP1 |
| **IIS Site** | Default Web Site |
| **IIS Sub-Application** | `/reporting` |
| **Environment** | UAT (`ASPNETCORE_ENVIRONMENT=UAT`) |
| **Reviewing Agents** | developer-dotnet · validator-iis-deploy · developer-integration · architect-system-design |
| **Last Updated** | March 2026 |

---

> ⚠️ **SHARED SERVER WARNING**
>
> UAT and Production run on the **same server** (SRXWEBAPP1) at the **same IIS application path** (`/reporting`).
> The only difference between environments is `ASPNETCORE_ENVIRONMENT` in the deployed `web.config`.
> An incorrect value here will either expose Swagger to Production users or apply production rate limits to UAT.
> **Double-check Section 6.3 before starting the app pool.**

> **Deploy Order (Critical)**
>
> 1. Deploy Reporting Service **FIRST** → verify health check → proceed to step 2
> 2. Deploy SM-Portal **SECOND** → verify `/reports` route in SM-Portal
>
> SM-Portal's `ReportingController` proxies to this service. If Reporting Service is not healthy first, SM-Portal returns 502/503 during its own deployment window.

---

## Section 1: Prerequisites

Verify every item before starting. Fix any failures before proceeding.

| # | Item | How to Verify | Notes |
|---|---|---|---|
| 1 | .NET 8 **Hosting Bundle** installed (not just Runtime) | `dotnet --info` on SRXWEBAPP1 — look for `Microsoft.AspNetCore.App 8.*` | SDK is not needed on the server |
| 2 | ASP.NET Core Module v2 registered in IIS | `& "$env:windir\system32\inetsrv\appcmd.exe" list module /name:AspNetCoreModuleV2` — must return one result | Required for InProcess hosting |
| 3 | **IIS URL Rewrite Module 2.1** installed | `Get-WebGlobalModule -Name "RewriteModule"` — must return a result | Required for HTTP→HTTPS redirect in `web.config`. **SM-Portal lesson #7: install before deploying.** |
| 4 | App pool `ReportingService` exists | `& "$env:windir\system32\inetsrv\appcmd.exe" list apppool /name:ReportingService` | Must return exactly one result |
| 5 | App pool: No Managed Code, Integrated Pipeline | `& "$env:windir\system32\inetsrv\appcmd.exe" list apppool "ReportingService" /text:*` — check `managedRuntimeVersion=""` and `pipelineMode="Integrated"` | CLR version must be blank (not v4.0) |
| 6 | App pool idle timeout disabled | Same command — check `idleTimeout="00:00:00"` | Required for 23:00 UTC exchange rate sync timer. Default 20 min kills the running process. **Not sufficient on its own — see 6a/6b.** |
| 6a | App pool `startMode=AlwaysRunning` | Same command — check `startMode="AlwaysRunning"` | **Critical.** Default `OnDemand` means IIS only starts the worker on the first HTTP request, so the sync timer never exists until someone calls the API. Caused a 4-month silent outage (Apr–Aug 2026). Fix: `appcmd set apppool /apppool.name:"ReportingService" /startMode:AlwaysRunning` |
| 6b | Application preload enabled | `& "$env:windir\system32\inetsrv\appcmd.exe" list app "Default Web Site/reporting" /text:*` — check `preloadEnabled:true` | Warms the worker process on pool start. Fix: `appcmd set app "Default Web Site/reporting" /preloadEnabled:true` |
| 6c | Worker process running with no traffic | `& "$env:windir\system32\inetsrv\appcmd.exe" list wp` | **Decisive check.** `ReportingService` must be listed. App pool state `Started` only means "allowed to start" — it does not mean a process exists. |
| 7 | **Only one app pool** assigned to `/reporting` | `& "$env:windir\system32\inetsrv\appcmd.exe" list app "Default Web Site/reporting"` — must show `applicationPool:ReportingService` | **SM-Portal lesson #3:** two pools on the same app causes 500.35 errors. If this shows `DefaultAppPool`, fix it: `appcmd set app "Default Web Site/reporting" /applicationPool:"ReportingService"` |
| 8 | IIS sub-application `/reporting` exists and points to publish folder | IIS Manager → Default Web Site → reporting → Physical Path | Physical path must be the active publish folder |
| 9 | HTTPS binding on Default Web Site | IIS Manager → Default Web Site → Bindings → HTTPS 443 present with valid certificate | Required — WR-5 |
| 10 | Windows Authentication **disabled** on ReportingService | IIS Manager → /reporting → Authentication → Windows Authentication = Disabled | Reporting-Service uses API Key auth only. Enabling Windows Auth causes authentication middleware conflicts. |
| 11 | IBM i ODBC DSN `MOVEX_PROD` configured for app pool identity | On SRXWEBAPP1: ODBC Data Sources (32/64-bit) → System DSN tab → `MOVEX_PROD` present | App pool identity must have permission to use the DSN |
| 12 | Secrets file present | `Test-Path "C:\ProgramData\SRX\Reporting\secrets.json"` → must return `True` | If missing, run Section 5 before proceeding |
| 13 | NTFS ACLs correct on secrets file | `Get-Acl "C:\ProgramData\SRX\Reporting\secrets.json" \| Format-List` — expect `IIS AppPool\ReportingService: Read`, `Administrators: FullControl`, `SYSTEM: FullControl`, no other entries | Run Section 5 to fix if wrong |
| 14 | Outbound HTTPS to rba.gov.au:443 allowed | `Test-NetConnection rba.gov.au -Port 443` from SRXWEBAPP1 | Required for nightly exchange rate sync |
| 15 | `dotnet build` clean in Release | On developer machine: `dotnet build Reporting.Service.sln -c Release` → zero errors, zero warnings | Fix before deployment — do not deploy a dirty build |
| 16 | `dotnet test` all green | `dotnet test --collect:"XPlat Code Coverage"` → all tests pass, coverage ≥ 80% | Check `tests/Reporting.Tests/TestResults/` |

---

## Section 2: Pre-Deploy Validation

### 2.1 Governance Gates (UAT)

UAT has lighter governance gates than Production. The following gates are **NOT required for UAT**:

| Gate | Required for UAT? | Required for Production? |
|---|---|---|
| ADR-008: Architecture Team approval for DB2 CCURRA write | ❌ No | ✅ Yes — before first Production deploy |
| T21a: QuestPDF Community Edition IT/Legal review | ❌ No | ✅ Yes — before first Production deploy |
| WR-1: Audit logging Architecture Team approval | ❌ No | ✅ Yes |
| ADR-006: Azure Key Vault IT confirmation | ❌ No | ✅ Yes |

The following gates **ARE required for UAT**:

```
[ ] WR-3: README.md present with all mandatory sections
[ ] WR-5: HTTPS binding configured (verified in Prerequisites #9)
[ ] WR-6: All runbooks in docs/runbooks/ — not docs/ directly
[ ] ADR-009: NTFS-protected secrets.json in use (verified in Prerequisites #12–#13)
```

### 2.2 Route Audit

Controller routes must NOT include the IIS sub-application prefix `/reporting`.

```powershell
# Run from project root — must return zero matches
Select-String -Path "src\Reporting.Api\Controllers\*.cs" -Pattern '\[Route\("reporting/'
# Expected output: (no output — zero matches)
```

**Why:** IIS strips the `/reporting` prefix before forwarding requests to ASP.NET Core. If controllers include `reporting/` in routes, all API calls return 404. This burned 8 hours on SM-Portal Issue #7.

### 2.3 Security Check

```powershell
# Verify secrets.json does not contain placeholder values on SRXWEBAPP1
$secrets = Get-Content "C:\ProgramData\SRX\Reporting\secrets.json" | ConvertFrom-Json
if ($secrets.ApiKeys.Primary -match "^(your-|placeholder|changeme|TODO)") {
    Write-Error "secrets.json contains placeholder API key — run Setup-ServerSecrets.ps1"
}
```

### 2.4 Build Verification

```powershell
# On developer machine — from project root
dotnet build Reporting.Service.sln -c Release
dotnet test --collect:"XPlat Code Coverage"
```

Both must succeed before proceeding to Section 4.

---

## Section 3: Understanding the UAT Environment

When `ASPNETCORE_ENVIRONMENT=UAT` is active, `appsettings.UAT.json` is merged on top of `appsettings.json`:

| Setting | Production (`appsettings.json`) | UAT override (`appsettings.UAT.json`) |
|---|---|---|
| `Swagger:EnableUI` | `false` | `true` — Swagger UI accessible at `/reporting/swagger` |
| `Serilog:MinimumLevel:Default` | `Information` | `Debug` — verbose logging |
| `RateLimiting:PermitLimit` | `60` req/min | `120` req/min |
| `ExchangeRateSync:SkipWeekends` | `true` | `false` — sync runs on weekends in UAT |
| Log file retention | 30 days | 7 days |

**Diagnostic tip:** If Swagger is not accessible after deployment, the environment variable is still `Production`. If Swagger IS accessible on a supposed Production deployment, the environment variable was not updated from UAT — fix immediately.

---

## Section 4: Build and Publish

```powershell
# From project root on developer machine
dotnet publish src/Reporting.Api -c Release -o publish/
```

**Verify the publish output contains:**

```
publish/
├── Reporting.Api.dll          ← main assembly
├── Reporting.Api.exe          ← self-contained executable
├── web.config                 ← MUST be edited in Section 6.3 before deploy
├── appsettings.json
├── appsettings.UAT.json       ← must be present — activates UAT overrides
├── config/
│   └── report-catalog.json   ← must be present — loaded at startup
└── logs/                      ← created at runtime; may not exist yet
```

> If `appsettings.UAT.json` is missing from the publish output, check that it is set to
> `CopyToPublishDirectory=PreserveNewest` in the `.csproj`. Do NOT deploy without it.

---

## Section 5: Secrets Setup

**Skip this section if** `secrets.json` already exists and has correct values (Prerequisites #12–#13 passed).

Run this section if: first-time deployment, secrets rotation needed, or ACLs were found incorrect.

### 5.1 Generate API Keys

Run on the developer machine (not the server):

```powershell
.\scripts\generate-api-key-simple.ps1
# Copy the generated key values — you will enter them in Step 5.2
```

### 5.2 Create or Update Secrets File on SRXWEBAPP1

```powershell
# Run as Administrator on SRXWEBAPP1 — from the project root or publish folder
.\scripts\Setup-ServerSecrets.ps1
```

The script will:
1. Create `C:\ProgramData\SRX\Reporting\` if it does not exist
2. Prompt for `ApiKeys:Primary`, `ApiKeys:Admin`, and `DataSources:Db2:ConnectionString`
3. Write the JSON secrets file
4. Apply NTFS ACLs: `IIS AppPool\ReportingService` (Read), `Administrators` (FullControl), `SYSTEM` (FullControl) — all other accounts denied

The secrets file structure it creates:

```json
{
  "ApiKeys": {
    "Primary": "<generated-key>",
    "Admin": "<generated-admin-key>"
  },
  "DataSources": {
    "Db2": {
      "ConnectionString": "<odbc-connection-string>"
    }
  }
}
```

> **Note:** The SQL Server connection string is NOT in the secrets file — it is in `appsettings.json`
> under `DataSources:SqlServer:ConnectionString` with the server address `150.3.20.116`. Only the
> DB2 connection string contains credentials.

### 5.3 Verify ACLs After Setup

```powershell
Get-Acl "C:\ProgramData\SRX\Reporting\secrets.json" | Format-List

# Expected output — only these three entries:
# IIS AppPool\ReportingService  Allow  Read, ReadAndExecute, Synchronize
# BUILTIN\Administrators        Allow  FullControl
# NT AUTHORITY\SYSTEM           Allow  FullControl
```

If any other account appears (e.g., `Everyone`, `NETWORK SERVICE`, `Domain Users`), re-run the setup script.

---

## Section 6: Deploy to IIS

### 6.1 Confirm and Stop the App Pool

**First, confirm which pool is assigned to the application.** Do not skip this — stopping the wrong pool leaves the DLL locked.

```powershell
# Confirm the correct pool name
& "$env:windir\system32\inetsrv\appcmd.exe" list app "Default Web Site/reporting"
# Expected: APP "Default Web Site/reporting" (applicationPool:ReportingService)
# If it shows DefaultAppPool — fix the assignment first (see Prerequisites #7)

# Now stop it
& "$env:windir\system32\inetsrv\appcmd.exe" stop apppool /apppool.name:"ReportingService"
```

> ⚠️ **DLL File Lock:** .NET InProcess hosting holds a file lock on `Reporting.Api.dll` while running.
> Copying files while the app pool is running causes `Access Denied` errors.
> **Always stop the pool before copying.** SM-Portal wasted time on this exact issue (Lesson #3).

### 6.2 Backup and Copy Publish Output

```powershell
# Name the backup folder with a timestamp for easy rollback identification
$timestamp = Get-Date -Format "yyyyMMdd_HHmm"
$iisPath = "C:\inetpub\wwwroot\reporting"           # ← adjust to actual IIS physical path
$backupPath = "C:\Deployments\Reporting\ReportingService_$timestamp"

# Backup current deployment (keep for 1 week minimum)
if (Test-Path $iisPath) {
    Copy-Item -Path $iisPath -Destination $backupPath -Recurse
    Write-Host "Backup created at: $backupPath"
}

# Copy new publish output
Copy-Item -Path "publish\*" -Destination $iisPath -Recurse -Force
Write-Host "Deployment files copied to: $iisPath"
```

### 6.3 Edit web.config for UAT

> ⚠️ **This is the critical UAT-specific step.** The `web.config` committed to source control has
> `ASPNETCORE_ENVIRONMENT=Production`. For UAT, you must manually edit the deployed copy on the server.

Open `web.config` in the IIS physical path and verify it contains:

```xml
<environmentVariables>
  <!-- FOR UAT: value="UAT"  |  FOR PRODUCTION: value="Production" -->
  <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="UAT" />

  <!-- ALWAYS PRESENT — do NOT remove. Without this, app cannot find config/report-catalog.json -->
  <!-- (ContentRoot defaults to C:\Windows\System32 under IIS — SM-Portal Issue #1) -->
  <environmentVariable name="ASPNETCORE_CONTENTROOT" value="%APPL_PHYSICAL_PATH%" />

  <!-- Path to protected secrets file. File is outside deployment folder — survives redeployments. -->
  <environmentVariable name="REPORTING_SECRETS_PATH"
                       value="C:\ProgramData\SRX\Reporting\secrets.json" />
</environmentVariables>
```

**Critical rules for `web.config`:**

| Rule | Reason |
|---|---|
| Do NOT set `ASPNETCORE_URLS` | Under InProcess hosting, IIS controls port binding. Setting this causes Kestrel to try binding independently → `SocketException: address already in use` (SM-Portal Issue #4) |
| Do NOT remove `ASPNETCORE_CONTENTROOT` | Without it, ContentRootPath resolves to `C:\Windows\System32` → `FileNotFoundException` on `config/report-catalog.json` (SM-Portal Issue #1) |
| Keep `stdoutLogEnabled="false"` | Enabling this writes unbounded log files to `logs\stdout*.log`. Enable only temporarily for startup debugging, always revert. |
| Do NOT add `<remove name="aspNetCore" />` | Reporting-Service's `web.config` does not use the `<remove>` handler pattern. If you see duplicate handler errors in the Windows Event Log, add `<remove name="aspNetCore" />` immediately before the `<add>` handler — but only then. |

### 6.4 Verify App Pool Configuration

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" list apppool "ReportingService" /text:*
```

Confirm all three:
- `managedRuntimeVersion:` → must be **empty** (No Managed Code). If it shows `v4.0`, fix: `appcmd set apppool /apppool.name:"ReportingService" /managedRuntimeVersion:""`
- `idleTimeout:` → must be **`00:00:00`** (disabled). If it shows `00:20:00`, fix: `appcmd set apppool /apppool.name:"ReportingService" /processModel.idleTimeout:00:00:00`
- `pipelineMode:` → must be **`Integrated`**

### 6.5 Start the App Pool

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" start apppool /apppool.name:"ReportingService"

# Allow ~5 seconds for the app to initialise, then proceed to Section 7
Start-Sleep -Seconds 5
```

---

## Section 7: Post-Deploy Verification (Smoke Tests)

Run in order. Diagnose failures using the table at the end of this section before proceeding.

Replace `<primary-key>` with the `ApiKeys:Primary` value from `secrets.json`.

**Step 1 — Health check** (no API key required)

```powershell
curl -s https://srxwebapp1/reporting/api/v1/health
# Expected: {"status":"Healthy", ...}
```

**Step 2 — Data sources** (confirms DB2 and SQL Server reachable)

```powershell
curl -s -H "X-API-Key: <primary-key>" https://srxwebapp1/reporting/api/v1/health/data-sources
# Expected: both "IBM i DB2" and "SQL Server DW" show status "Healthy"
```

**Step 3 — Swagger UI** (confirms UAT environment is active)

```
Browse to: https://srxwebapp1/reporting/swagger
Expected: Swagger UI loads with full API documentation.
If Swagger returns 404 or 401: web.config still has ASPNETCORE_ENVIRONMENT=Production.
Stop pool → fix web.config → start pool → retry.
```

**Step 4 — Report catalog**

```powershell
curl -s -H "X-API-Key: <primary-key>" "https://srxwebapp1/reporting/api/v1/reports?domain=CostManagement"
# Expected: JSON array with exactly 3 report definitions
```

**Step 5 — Rate limit verification**

UAT rate limit is **120 req/min**. If the 429 fires at ~61 requests, Production config is active (Swagger may have loaded but SkipWeekends and rate limit config are wrong).

```powershell
$hitAt = $null
1..125 | ForEach-Object {
    $r = Invoke-WebRequest -Uri "https://srxwebapp1/reporting/api/v1/reports" `
         -Headers @{"X-API-Key"="<primary-key>"} -UseBasicParsing -ErrorAction SilentlyContinue
    if ($r.StatusCode -eq 429 -and -not $hitAt) { $hitAt = $_; Write-Host "429 at request $_ (expected ~121)" }
}
if (-not $hitAt) { Write-Host "No 429 received in 125 requests — rate limit may be misconfigured" }
```

**Step 6 — Notify SM-Portal team**

Once all 5 smoke tests pass: notify the SM-Portal team that Reporting Service is healthy and they may proceed with their deployment.

**Step 7 — SM-Portal verification (after SM-Portal deploys)**

Verify the `/reports` route in SM-Portal loads report data proxied from this service.

---

### Smoke Test Failure Diagnostics

| HTTP Status | Meaning | First action |
|---|---|---|
| **502 Bad Gateway** | App pool not running, or app crashed at startup | Check Windows Event Log → Application → Source: "IIS ANCM Web Server". Do NOT chase API key issues — 502 means the app never started. |
| **404 on /api/v1/health** | `ASPNETCORE_CONTENTROOT` wrong, or app not routing correctly | Check Windows Event Log. Enable `stdoutLogEnabled="true"` temporarily, restart pool, check `logs\stdout*.log`, then revert. |
| **401 on /api/v1/reports** | API key rejected | Verify `X-API-Key` header value matches `ApiKeys:Primary` in `secrets.json`. 401 ≠ 502. If you're getting 502, see row above. |
| **503 Degraded** | App is up but data source unhealthy | Run `/api/v1/health/data-sources` to identify which source is failing. Check IBM i ODBC DSN and SQL Server 150.3.20.116 connectivity. |
| **429 firing at ~61** | Production rate limit active (60/min) | `ASPNETCORE_ENVIRONMENT` is `Production`, not `UAT`. Fix `web.config`. |
| **Swagger 404** | `ASPNETCORE_ENVIRONMENT=Production` | Fix `web.config` → set to `UAT` → restart pool. |

---

## Section 7b: Register the Exchange Rate Sync Scheduled Task

**Required on every server. Skipping this has already caused one silent four-month outage.**

The RBA exchange rate sync runs on an in-process timer at 23:00 UTC. That timer only ticks while
the IIS worker process is alive — and this service receives no traffic of its own overnight. If
the worker is stopped (app pool set to `OnDemand`, a deployment, a manual stop), the timer stops
with it and **nothing restarts it**. Rates then silently stop updating: the API keeps answering
queries, it just serves increasingly stale data.

That is exactly what happened in 2026 — a misconfigured app pool left the sync dead for four
months with no error anywhere.

The scheduled task is the backstop. It POSTs to `/api/v1/exchange-rates/sync`, and because the
inbound HTTP request itself starts the worker, it does not depend on IIS keeping anything alive.
It survives an app-pool configuration regression, which the in-process timer cannot.

```powershell
# On the server, elevated
cd C:\Projects\Reporting-Service\scripts
.\Register-ExchangeRateSyncTask.ps1
```

Verify:

```powershell
Get-ScheduledTask     -TaskName "ReportingService-ExchangeRateSync" | Select-Object TaskName, State
Get-ScheduledTaskInfo -TaskName "ReportingService-ExchangeRateSync" |
    Select-Object LastRunTime, LastTaskResult, NextRunTime
```

- `State` = `Ready`
- `LastTaskResult` = `0` (success). Non-zero means the sync endpoint returned an error — the
  endpoint deliberately returns **503** when sync is disabled or failed, so the task's result code
  surfaces it rather than reporting a false success.

Force a run to confirm end to end:

```powershell
Start-ScheduledTask -TaskName "ReportingService-ExchangeRateSync"
Start-Sleep -Seconds 20
Get-ScheduledTaskInfo -TaskName "ReportingService-ExchangeRateSync" |
    Select-Object LastRunTime, LastTaskResult
```

A weekend run reports `Skipped` and is still a success — `SkipWeekends` means there is genuinely
no RBA rate to fetch.

> **Why both a timer and a task?** They fail differently. The timer is precise but dies with the
> worker; the task is external but coarser. The sync is idempotent — `Db2ExchangeRateWriter` skips
> dates already present — so in the normal case the 23:30 task call is a no-op confirming the
> 23:00 timer already ran.

### Backfilling rates missed during an outage

**The sync endpoint cannot backfill.** `RbaApiClient.FetchLatestRateAsync` returns only the most
recent rate, so `POST /api/v1/exchange-rates/sync` inserts *one date per currency* no matter how
many are missing. Calling it repeatedly does not walk backwards through a gap.

Use the generator in `tools/` to produce INSERT statements from RBA's published history:

```powershell
cd C:\Projects\Reporting-Service\tools
python generate-historical-rates-sql.py --csv --from-year 2026 --output rates-2026.sql
```

`--csv` with no filename downloads live from RBA. Granularity is per-year, so trim the output to
the missing dates before running it — the whole-year file is safe (every statement carries a
`NOT EXISTS` guard) but is hard to review and mostly redundant.

Existing backfill files, each continuing where the previous ends:

| File | Period | Cause |
|---|---|---|
| `ccurra-backfill-gap-2026-04-02-to-2026-08-17.sql` | Apr 2 – Aug 17 2026 | App pool `startMode=OnDemand`; worker never started |
| `ccurra-backfill-gap-2026-08-18-to-2026-09-17.sql` | Aug 18 – Sep 17 2026 | Fix not yet deployed; timer still not running |

**Every statement is idempotent** — a `NOT EXISTS` guard on the primary key
`(CUCONO,CUDIVI,CUCUCD,CUCRTP,CUCUTD)`. Re-running inserts nothing, and dates the sync has already
written are skipped. Check what is genuinely missing before running:

```sql
SELECT COUNT(*) FROM mvxcdta.CCURRA
 WHERE CUCONO=100 AND CUDIVI='D' AND CUCRTP='99'
   AND CUCUTD BETWEEN 20260818 AND 20260917;
-- Each file's header states the expected total; the difference is what will be inserted.
```

Verify afterwards with the per-currency query in the file header — expect the same row count for
every currency, with weekends and AU public holidays legitimately absent (RBA does not publish on
those days; `FallbackDays=5` covers them at query time).

### Detecting a stalled sync

Neither mechanism alerts if both fail. Check rate freshness directly:

```powershell
$r = curl -s -H "X-API-Key: <primary-key>" `
    "https://srxwebapp1/reporting/api/v1/exchange-rates/USD/$(Get-Date -Format 'yyyy-MM-dd')" |
    ConvertFrom-Json
$r | Select-Object currency, requestedDate, effectiveDate, rate, usedFallback, lastSyncUtc
```

`lastSyncUtc` older than ~48 hours on a weekday means the sync has stalled — check the worker
process first (`Get-Process w3wp`), then the scheduled task's `LastTaskResult`.

> **Related:** MyInvois-Service hit the same class of failure on the same server in September 2026
> — an in-process `BackgroundService` scheduler that stopped and could not restart because IIS
> Application Initialization was not installed, so `AlwaysRunning` was silently ignored. Any
> service on this box that does work on a timer rather than in response to requests needs an
> external trigger. See `MyInvois-Service/docs/TROUBLESHOOTING.md`.

---

## Section 8: Rollback Procedure

Use this procedure if smoke tests fail and the issue cannot be resolved quickly.

**Step 1 — Stop the app pool**

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" stop apppool /apppool.name:"ReportingService"
```

**Step 2 — Swap physical path to previous backup**

```powershell
# Replace with the actual backup path created in Section 6.2
$backupPath = "C:\Deployments\Reporting\ReportingService_YYYYMMDD_HHmm"

& "$env:windir\system32\inetsrv\appcmd.exe" set app "Default Web Site/reporting" `
    /physicalPath:"$backupPath"

Write-Host "IIS physical path swapped to: $backupPath"
```

**Step 3 — Start the pool and re-run smoke tests**

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" start apppool /apppool.name:"ReportingService"
Start-Sleep -Seconds 5
# Re-run Section 7 smoke tests
```

**Retention policy:** Keep all deployment backup folders for a minimum of **1 week**. After that, clean up folders older than 1 week unless an active rollback is in progress.

**Naming convention:** `ReportingService_YYYYMMDD_HHmm` (e.g., `ReportingService_20260320_1430`)

---

## Section 9: Agent Sign-Off

Complete before marking UAT deployment as done.

| Agent | Responsibility in This Deployment | Sign-Off |
|---|---|---|
| **developer-dotnet** | Build clean (zero warnings), `web.config` ASPNETCORE_ENVIRONMENT=UAT verified, `appsettings.UAT.json` present in publish output | [ ] |
| **validator-iis-deploy** | App pool No Managed Code + idle timeout=0, only one pool assigned to `/reporting`, CONTENTROOT present, smoke tests Steps 1–5 passed | [ ] |
| **validator-iis-deploy** | `ReportingService-ExchangeRateSync` scheduled task registered, `State=Ready`, forced run returned `LastTaskResult=0` (Section 7b) | [ ] |
| **developer-integration** | `/api/v1/health/data-sources` shows both IBM i DB2 and SQL Server DW healthy | [ ] |
| **architect-system-design** | ADR-009 (secrets file) in place and verified; ADR-008 not required for UAT (confirmed) | [ ] |

---

## Reference: Key Paths and Commands

| Item | Value |
|---|---|
| IIS physical path | Configured in IIS Manager — verify before copying |
| Secrets file | `C:\ProgramData\SRX\Reporting\secrets.json` |
| Backup folder root | `C:\Deployments\Reporting\` (recommended) |
| App pool name | `ReportingService` |
| Health check URL | `https://srxwebapp1/reporting/api/v1/health` |
| Swagger URL (UAT only) | `https://srxwebapp1/reporting/swagger` |
| Windows Event Log | Event Viewer → Windows Logs → Application → Source: "IIS ANCM Web Server" |
| Serilog logs | `<publish-path>\logs\reporting-service-YYYYMMDD.log` |

---

*For runtime troubleshooting see [TROUBLESHOOTING.md](TROUBLESHOOTING.md). For Production deployment additional gates see [ai/checklists/pre-deployment.md](../../ai/checklists/pre-deployment.md).*
