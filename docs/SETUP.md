# Setup Guide — Reporting Service

See [README.md](../README.md) for quick start. This guide covers detailed setup steps.

## IBM i ODBC Driver Setup

1. Install IBM i Access Client Solutions (iACS)
2. Configure ODBC Data Source:
   - Open ODBC Data Source Administrator (64-bit)
   - Add System DSN → IBM i Access ODBC Driver
   - DSN Name: `MOVEX_PROD` (matches `appsettings.json DataSources:Db2:Dsn`)
   - Server: [IBM i hostname/IP]
   - Default Library: mvxcdta

## User Secrets Setup (Development)

```bash
cd src/Reporting.Api
dotnet user-secrets init
dotnet user-secrets set "ApiKeys:Primary" "generate-a-secure-key-here"
dotnet user-secrets set "ApiKeys:Admin" "generate-a-secure-admin-key-here"
dotnet user-secrets set "DataSources:Db2:UserId" "your-db2-user"
dotnet user-secrets set "DataSources:Db2:Password" "your-db2-password"
dotnet user-secrets set "DataSources:SqlServer:ConnectionString" "Server=150.3.20.116;Database=MovexDatawarehouse;Integrated Security=true;"
```

## Pre-Commit Hooks

```powershell
.\setup-hooks.ps1
```

Hooks enforce:
- `ai/memory/00-skills-audit.md` must exist when src/ files are staged
- Skills referenced in code comments

---

## Local Smoke Test — Sprint 1 Done-When Gates

Sprint 1 has no DB2/SQL Server dependency. Only the API key secret is required to run.

### 1. Start the service

```bash
dotnet run --project src/Reporting.Api
```

Confirm the catalog loaded in console output:

```
[HH:mm:ss INF]  Report catalog loaded. Starting SRX Reporting Service
```

`ASPNETCORE_ENVIRONMENT=Development` is set automatically by `launchSettings.json` when
using `dotnet run`. URLs:

| Profile | URL                        |
|---------|----------------------------|
| http    | http://localhost:5160      |
| https   | https://localhost:7174     |

### 2. Health check (no API key needed)

```bash
curl http://localhost:5160/api/v1/health
```

Expected: `{"status":"Healthy",...}`

### 3. Gate 1 — catalog returns 3 CostManagement reports

```bash
curl -H "X-API-Key: <your-primary-key>" \
     "http://localhost:5160/api/v1/reports?domain=CostManagement"
```

Expected: JSON array with 3 items (`cost.average-cost-snapshot`, `cost.wac-history`,
`cost.cost-variance-analysis`).

### 4. Gate 2 — rate limit returns 429 after 60 req/min

```powershell
1..65 | ForEach-Object {
    $r = Invoke-WebRequest -Uri "http://localhost:5160/api/v1/reports" `
         -Headers @{"X-API-Key"="<your-primary-key>"}
    Write-Host "$_ -> $($r.StatusCode)"
}
# Requests ~61+ should return 429
```

### 5. Gate 3 — invalid catalog retains last-known-good

1. Temporarily add `"Catalog": { "ReloadIntervalSeconds": 10 }` to `appsettings.Development.json`
2. Restart the service
3. Corrupt `config/report-catalog.json` (e.g. delete a `"` character)
4. Wait ~15 seconds
5. `GET /api/v1/health` → status `Degraded`, body shows `"isServingLastKnownGood": true`
6. `GET /api/v1/reports` → still returns 3 reports (no crash)
7. Restore the file — health returns `Healthy` after the next reload cycle

### 6. Swagger UI

Browse to `http://localhost:5160/swagger`, click **Authorize**, enter your primary key.
All catalog endpoints are exercisable without any DB2/SQL Server connection in Sprint 1.

> If Swagger is not visible, see [Troubleshooting](runbooks/TROUBLESHOOTING.md#swagger-ui-not-visible).
