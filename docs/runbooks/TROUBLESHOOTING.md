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
