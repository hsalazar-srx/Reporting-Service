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

### IIS Issue 2 — App Pool Idle Timeout Kills Exchange Rate Sync

**Symptom:** Exchange rate sync fires successfully on the first night, then never again. Logs show
no sync attempt in subsequent nights. `GET /api/v1/exchange-rates/USD/<date>` returns stale data.

**Root cause:** IIS default idle timeout (20 minutes) recycles the app pool when there is no traffic
after 20 minutes. The `ExchangeRateSyncService` hosted service is terminated with the pool. IIS does
not restart the background service when the next HTTP request arrives — it only restarts the app.

**Fix:**
```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool `
    /apppool.name:"ReportingService" /processModel.idleTimeout:00:00:00
```

**Prevention:** Pre-deployment checklist 1.4 verifies `idleTimeout=00:00:00`.

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

### IIS Issue 9 — URL Rewrite Module Not Installed

**Symptom:** HTTP requests to `http://srxwebapp1/reporting/...` return 404 instead of redirecting to HTTPS.
Or the IIS site fails to start with a configuration error referencing `rewrite`.

**Root cause:** The committed `web.config` contains an HTTP→HTTPS rewrite rule that requires
IIS URL Rewrite Module 2.1 to be installed. If the module is missing, IIS cannot parse the `<rewrite>`
configuration block.

**Fix:**
1. Download and install **URL Rewrite Module 2.1** from Microsoft IIS downloads
2. Restart IIS: `iisreset`
3. Verify: `Get-WebGlobalModule -Name "RewriteModule"` — must return a result

**Prevention:** Prerequisites checklist item 3 in the UAT runbook checks for this module.
(SM-Portal Lesson #7 — installing this unblocked the HTTPS redirect)

---

## Log Locations

- **Application logs (Serilog):** `<publish-path>\logs\reporting-service-YYYYMMDD.log`
- **IIS stdout (startup debugging only):** `<publish-path>\logs\stdout_*.log` — enable in `web.config` temporarily, always revert
- **Windows Event Log (IIS/startup failures):** Event Viewer → Windows Logs → Application → filter Source = `IIS ANCM Web Server`
- **IIS configuration:** `%windir%\System32\inetsrv\config\applicationHost.config`
