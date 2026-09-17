# Troubleshooting Runbook — Reporting Service

## Common Issues

### Swagger UI Not Visible

**Symptom:** Browsing to `/swagger` returns 404 or 401; Swagger UI never loads.

**Root cause:** Swagger is gated on `Swagger:EnableUI = true`, which is only set in
`appsettings.Development.json`. If `ASPNETCORE_ENVIRONMENT` is not `Development`, the
setting stays `false` and the middleware is never registered.

**Check:** Look for `Development` in the startup console output. If absent:

```bash
# PowerShell — set before running
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/Reporting.Api
```

Or simply run via `dotnet run` (not a custom script) — `launchSettings.json` sets
`ASPNETCORE_ENVIRONMENT=Development` automatically for all three profiles.

**Correct dev URLs** (from `launchSettings.json`):
- `http://localhost:5160/swagger`
- `https://localhost:7174/swagger`

**Note:** Swagger is intentionally disabled in Production (WR-4). The `/swagger` path also
requires an API key in Production — this is by design.

---

### Health Check Returns 503

**Symptom:** `GET /api/v1/health` returns 503 or Degraded
**Check:**
1. `GET /api/v1/health/data-sources` — which data source is failing?
2. IBM i ODBC DSN connectivity: `isql MOVEX_PROD` from server
3. SQL Server DW: test connection to 150.3.20.116
4. IIS Application Pool running: check Windows Event Log

### 429 Too Many Requests

**Symptom:** Clients receiving 429
**Check:** Rate limit is 60 req/min per IP (fixed window). Expected for load testing.
**Fix:** Increase `RateLimiting:PermitLimit` in appsettings.json (requires approval).

### Report Execution Timeout

**Symptom:** `EXECUTION_TIMEOUT` error code in response
**Check:** `Report:DefaultExecutionTimeoutSeconds` (default 300). IBM i may be under load.
**Fix:** Increase `executionTimeoutSeconds` for the specific report in `config/report-catalog.json`.

### Catalog File Invalid / Last-Good-Config Active

**Symptom:** `GET /api/v1/health` returns `Degraded` with reason "catalog reload failed"
**Check:** Validate `config/report-catalog.json` with a JSON linter
**Fix:** Correct the JSON and wait for reload interval (default 60s), or restart IIS App Pool

### API Key Rejected (401)

**Symptom:** All requests return 401
**Check:** X-API-Key header present and matches configured value
**Fix:** Verify user secrets / Azure Key Vault value matches what client is sending

### DB2 Circuit Breaker Open

**Symptom:** Immediate `DATA_SOURCE_UNAVAILABLE` with no retry delay
**Check:** Circuit breaker opened after 5 failures. Wait 30s for auto-reset.
**Fix:** Investigate IBM i connectivity. Check Windows Event Log for OdbcException details.

## Log Locations

- Application logs: `logs/` folder in publish directory (Serilog)
- IIS stdout: only enable temporarily for startup debugging (`stdoutLogEnabled="true"` in web.config)
- Windows Event Log: Application channel

## Escalation

See [ai/workflows/development.md](../../ai/workflows/development.md) for escalation contacts.

---

## IIS Deployment Issues

These issues are sourced from live deployment incidents on SRXWEBAPP1 (SM-Portal and Reporting-Service).
Each one has caused significant downtime. Follow [docs/runbooks/uat-deployment.md](uat-deployment.md) to prevent them.

---

### IIS Issue 1 — ContentRoot Path Mismatch

**Symptom:** Application starts but `GET /api/v1/health` returns 503. Windows Event Log shows
`FileNotFoundException` or `DirectoryNotFoundException` referencing `config/report-catalog.json`.

**Root cause:** Under IIS, `ContentRootPath` defaults to `C:\Windows\System32` (or the IIS root),
not the application's physical folder. The catalog file at `config/report-catalog.json` cannot be found.

**Fix:**
```xml
<!-- In web.config environmentVariables block -->
<environmentVariable name="ASPNETCORE_CONTENTROOT" value="%APPL_PHYSICAL_PATH%" />
```
Restart the app pool after adding. This env var is committed to `web.config` — if it was overwritten
during deployment, copy it back.

**Prevention:** Check 1.3 in the pre-deployment checklist — CONTENTROOT is always verified before starting.

---

### IIS Issue 2 — Exchange Rate Sync Never Runs (Process Not Started)

**Symptom:** Exchange rate sync only ever runs when someone calls the API. Log files exist only on
days the service received traffic — entire weeks or months have no log file at all.
`GET /api/v1/exchange-rates/{ccy}/{date}` returns stale data or 404.

**Diagnostic — the log directory tells you immediately.** Serilog only creates
`logs/reporting-service-YYYYMMDD` on days the process was alive. Missing files are missing
*process lifetime*, not missing logging:

```powershell
Get-ChildItem "C:\inetpub\wwwroot\Reporting-Api\logs\" | Select-Object Name, LastWriteTime
```

Gaps spanning weekends = idle-timeout recycling. Gaps spanning **months** = the app pool is not
starting at all.

**Root cause:** `ExchangeRateSyncService` is started from `Program.cs` *before* `app.RunAsync()`.
Under `hostingModel="inprocess"`, that code runs only when IIS **launches the worker process** —
and with the default `startMode=OnDemand`, IIS launches it lazily on the first incoming HTTP
request. No request → no process → no `Program.cs` → no timer.

`idleTimeout=0` only prevents IIS from killing an *already-running* process. **It does not start
one.** Both settings are required, and `startMode` is the one that was missing.

Note this is a plain `System.Threading.Timer`, not an `IHostedService`/`BackgroundService`. Nothing
resurrects it — it lives and dies with the worker process.

**Fix — all three settings, together:**
```powershell
# 1. Start the pool with IIS, without waiting for a request  <- THE ACTUAL FIX
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool `
    /apppool.name:"ReportingService" /startMode:AlwaysRunning

# 2. Preload the application (warms the worker process)
& "$env:windir\system32\inetsrv\appcmd.exe" set app `
    "Default Web Site/reporting" /preloadEnabled:true

# 3. Do not recycle on idle
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool `
    /apppool.name:"ReportingService" /processModel.idleTimeout:00:00:00
```

> **Prerequisite — `AlwaysRunning` and `preloadEnabled` do nothing without IIS Application
> Initialization.** IIS accepts both settings and displays them as active, but silently ignores
> them if the module is absent. Confirm before trusting step 1:
> ```powershell
> Get-WindowsFeature Web-AppInit | Select-Object Name, InstallState   # must be Installed
> Install-WindowsFeature Web-AppInit                                  # if it reads Available
> ```
> Discovered on the same server in September 2026, when MyInvois-Service showed
> `Start Mode = AlwaysRunning` in IIS Manager while its scheduler stayed dead for three days.

**Verify:**
```powershell
# startMode must be AlwaysRunning, idleTimeout 00:00:00
& "$env:windir\system32\inetsrv\appcmd.exe" list apppool "ReportingService" /text:*

# Worker process must be listed even with zero traffic
& "$env:windir\system32\inetsrv\appcmd.exe" list wp
```

`list wp` is the decisive check. App pool state `Started` only means "allowed to start" — it does
**not** mean a process exists.

**Recovery after an outage:** CCURRA will have no rates for the dead period. Generate gap SQL with
`tools/generate-historical-rates-sql.py` (RBA F11.1 CSV covers Jan 2023 → present). All INSERTs carry
a `NOT EXISTS` guard, so re-running is safe. See
`tools/ccurra-backfill-gap-2026-04-02-to-2026-08-17.sql` for a worked example.

**Why this went unnoticed for months:** `HealthController` reads `LastStatus` from the in-memory
service, so it can only answer while the process is up. A health check can never observe the
"process is dead" state. Monitor **log file freshness** or CCURRA row recency instead — not the
health endpoint.

**Prevention:** Pre-deployment checklist verifies `startMode`, `preloadEnabled`, and `idleTimeout`.

---

### IIS Issue 3 — DLL File Lock Blocks Deployment

**Symptom:** `Copy-Item` or `robocopy` fails with `Access Denied` on `Reporting.Api.dll`.
The file is shown as in use by another process.

**Root cause:** .NET InProcess hosting holds a file lock on the application DLL while the process
is running. The app pool must be stopped before files can be replaced.

**Fix:** Stop the app pool first, then copy, then start.
```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" stop apppool /apppool.name:"ReportingService"
# Copy files here
& "$env:windir\system32\inetsrv\appcmd.exe" start apppool /apppool.name:"ReportingService"
```

**Prevention:** Section 6.1 of the UAT runbook requires confirming and stopping the pool before any copy.

---

### IIS Issue 4 — Kestrel Port Conflict (ASPNETCORE_URLS)

**Symptom:** App pool fails to start. Windows Event Log shows:
`Failed to bind to address http://0.0.0.0:5000: address already in use` or a `SocketException (10013)`.

**Root cause:** `ASPNETCORE_URLS` was set in `web.config` or the system environment. Under InProcess
hosting, IIS controls port binding. When Kestrel is also told to bind via `ASPNETCORE_URLS`, it
conflicts with IIS.

**Fix:** Remove `ASPNETCORE_URLS` from `web.config` entirely. Do not set it for InProcess hosting.
The committed `web.config` does not set it — if someone added it, remove it.

**Prevention:** Pre-deployment checklist 1.3 explicitly checks that `ASPNETCORE_URLS` is absent.

---

### IIS Issue 5 — stdoutLog Left Enabled

**Symptom:** Disk space consumed on SRXWEBAPP1. Log folder grows rapidly.
Files matching `logs\stdout_*.log` found in the deployment folder.

**Root cause:** `stdoutLogEnabled="true"` was set in `web.config` for startup debugging and not reverted.
stdout captures all console output with no rotation limit.

**Fix:** Set `stdoutLogEnabled="false"` in the deployed `web.config`. Delete accumulated stdout log files.

**Prevention:** Pre-deployment checklist 1.3 verifies `stdoutLogEnabled=false`. Only enable temporarily
when diagnosing a startup failure — always revert before leaving the server.

---

### IIS Issue 6 — Swagger Visible in Production

**Symptom:** Browsing `https://srxwebapp1/reporting/swagger` displays Swagger UI on the server
that should be Production.

**Root cause:** The deployed `web.config` still has `ASPNETCORE_ENVIRONMENT=UAT` from the previous
UAT deployment. `appsettings.UAT.json` sets `Swagger:EnableUI=true`, enabling the UI.

**Fix:**
1. Stop the app pool
2. Edit `web.config` — change `ASPNETCORE_ENVIRONMENT` from `UAT` to `Production`
3. Start the app pool
4. Verify `/swagger` returns 404 or 401

**Prevention:** Production checklist gate WR-4 (Section 2.1 and 2.3) explicitly verifies Swagger is inaccessible.

---

### IIS Issue 7 — Wrong App Pool Stopped During Deployment

**Symptom:** After stopping what appeared to be the correct app pool and copying files,
the DLL copy still fails with `Access Denied`. The app pool was `DefaultAppPool`, not `ReportingService`.

**Root cause:** IIS may assign `DefaultAppPool` to new applications by default, or it may have been
reassigned accidentally. Stopping `DefaultAppPool` does not release the lock on `Reporting.Api.dll`.

**Fix:** Always confirm the assigned pool BEFORE stopping:
```powershell
# Confirm which pool is assigned — look for applicationPool:ReportingService
& "$env:windir\system32\inetsrv\appcmd.exe" list app "Default Web Site/reporting"

# If it shows DefaultAppPool, fix the assignment first:
& "$env:windir\system32\inetsrv\appcmd.exe" set app "Default Web Site/reporting" `
    /applicationPool:"ReportingService"

# Then stop the correct pool:
& "$env:windir\system32\inetsrv\appcmd.exe" stop apppool /apppool.name:"ReportingService"
```

**Prevention:** Section 6.1 of the UAT runbook and pre-deployment checklist 1.4 both require confirming
the pool assignment before stopping. (SM-Portal Lesson #3)

---

### IIS Issue 8 — 502 vs 401 Confusion

**Symptom:** Smoke test returns 502 Bad Gateway. Engineer spends time verifying API keys,
rotating them, re-testing — but the 502 persists.

**Root cause:** 502 means the app pool is not running, the application crashed at startup,
or secrets.json cannot be read. It has nothing to do with the API key.
401 means the application is running but the API key is wrong or missing.

**Diagnostic table:**

| Status | Meaning | First action |
|---|---|---|
| **502** | App pool stopped / app crashed / `secrets.json` unreadable | Check Windows Event Log → Application → Source "IIS ANCM". If app started, check `secrets.json` ACLs. |
| **401** | App running, API key rejected | Verify `X-API-Key` header value matches `ApiKeys:Primary` in `secrets.json`. |
| **503** | App running but a data source is unhealthy | Run `/api/v1/health/data-sources` to identify which source failed. |
| **404 on /api/v1/health** | App may not be routing correctly or `CONTENTROOT` is wrong | Enable `stdoutLogEnabled` temporarily, check logs, revert. See IIS Issue 1. |

**Prevention:** Section 7 smoke test diagnostics table in the UAT runbook explicitly maps each status
to the correct first action. (SM-Portal Lesson #7)

---
## Log Locations

- **Application logs (Serilog):** `<publish-path>\logs\reporting-service-YYYYMMDD.log`
- **IIS stdout (startup debugging only):** `<publish-path>\logs\stdout_*.log` — enable in `web.config` temporarily, always revert
- **Windows Event Log (IIS/startup failures):** Event Viewer → Windows Logs → Application → filter Source = `IIS ANCM Web Server`
- **IIS configuration:** `%windir%\System32\inetsrv\config\applicationHost.config`
