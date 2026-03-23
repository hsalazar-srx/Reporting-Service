# Reporting Service

**Type:** .NET 8.0 ASP.NET Core REST API
**Phase:** RSAI-1 — Cost Management (Sprint 1 in progress)
**Purpose:** Replace Crystal Reports across 6 manufacturing domains; deliver JSON, Excel, and PDF reports from MOVEX IBM i DB2 data.

---

## Technology Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 8.0 ASP.NET Core |
| Data access | Dapper (ODBC for DB2, SqlClient for SQL Server) |
| PDF generation | QuestPDF Community (pending license review) |
| Excel generation | ClosedXML (MIT) |
| Resilience | Polly v8 (retry + circuit breaker) |
| Logging | Serilog (structured JSON + CorrelationId) |
| Testing | xUnit + FluentAssertions + Moq (≥80% coverage) |
| Auth | API Key (X-API-Key header) |
| Config | dotnet user-secrets (dev) / NTFS secrets.json (server — ADR-009) |

---

## Prerequisites

- .NET 8.0 SDK
- IBM i ODBC driver (iAccess / IBM i Access Client Solutions)
- Access to MOVEX AS/400 IBM i (DSN configured)
- Access to MovexDatawarehouse SQL Server (150.3.20.116)
- Access to SRXWEBAPP1 with IIS admin rights (UAT/Production deployments)

---

## Setup (Development)

1. Clone repository and restore dependencies:
   ```bash
   git clone <repo-url>
   cd Reporting-Service
   dotnet restore
   ```

2. Configure user secrets:
   ```bash
   cd src/Reporting.Api
   dotnet user-secrets init
   dotnet user-secrets set "ApiKeys:Primary" "your-key-here"
   dotnet user-secrets set "ApiKeys:Admin" "your-admin-key-here"
   dotnet user-secrets set "DataSources:Db2:Password" "your-db2-password"
   dotnet user-secrets set "DataSources:SqlServer:ConnectionString" "Server=150.3.20.116;..."
   ```

3. Activate pre-commit hooks:
   ```powershell
   .\setup-hooks.ps1
   ```

4. Run the service:
   ```bash
   dotnet run --project src/Reporting.Api
   ```

5. Browse API (Development only — Swagger is disabled in Production):
   - HTTP:  `http://localhost:5160/swagger`
   - HTTPS: `https://localhost:7174/swagger`

   > The `ASPNETCORE_ENVIRONMENT=Development` variable is set automatically by `launchSettings.json`
   > when using `dotnet run`. If Swagger is not visible, see [Troubleshooting](docs/runbooks/TROUBLESHOOTING.md).

---

## Running Tests

```bash
dotnet test --collect:"XPlat Code Coverage"
```

Coverage report: `tests/Reporting.Tests/TestResults/`

---

## Environment Configuration

See [ai/patterns/configuration.md](ai/patterns/configuration.md) for full `appsettings.json` structure.

**Key settings:**
- `DataSources:Db2:Schema` — MOVEX DB2 schema (e.g., `mvxcdta`)
- `DataSources:Db2:DefaultTimeoutSeconds` — 300 (configurable per report in catalog)
- `Report:DefaultExecutionTimeoutSeconds` — 300
- `RateLimiting:FixedWindow:PermitLimit` — 60 req/min
- `Swagger:EnableUI` — `false` in production

---

## Deployment

The service runs in two server environments — both on SRXWEBAPP1 under IIS at `/reporting`.
The environment is toggled via `ASPNETCORE_ENVIRONMENT` in the deployed `web.config`.

| Environment | How to Run | Swagger | Rate Limit | Secrets |
|---|---|---|---|---|
| Development | `dotnet run --project src/Reporting.Api` | Enabled (`localhost:5160/swagger`) | 60 req/min | `dotnet user-secrets` |
| UAT | IIS on SRXWEBAPP1 — `ASPNETCORE_ENVIRONMENT=UAT` | Enabled (`/reporting/swagger`) | 120 req/min | NTFS secrets.json (ADR-009) |
| Production | IIS on SRXWEBAPP1 — `ASPNETCORE_ENVIRONMENT=Production` | **Disabled** (WR-4) | 60 req/min | NTFS secrets.json (ADR-009) |

> **Deploy order:** Always deploy Reporting Service **FIRST**, verify health check, then deploy SM-Portal.

- [UAT Deployment Runbook](docs/runbooks/uat-deployment.md) — step-by-step IIS deployment guide
- [Deployment Guide](docs/DEPLOYMENT.md) — environment comparison, commands, secrets management, rollback

---

## API Endpoints

| Method | Route | Description |
|---|---|---|
| GET | `/api/v1/health` | Health check (no auth required) |
| GET | `/api/v1/health/data-sources` | Data source connectivity check |
| GET | `/api/v1/reports` | List all reports |
| GET | `/api/v1/reports?domain={domain}` | List reports by domain |
| GET | `/api/v1/reports/{id}` | Get report definition |
| POST | `/api/v1/reports/{id}/execute` | Execute report (JSON \| Excel \| PDF) |
| POST | `/api/v1/reports/{id}/preview` | Preview report (always JSON) |
| GET | `/api/v1/exchange-rates/{currency}/{date}` | SPOT rate for a date (e.g. `USD/2026-03-19`) |

---

## Exchange Rate Sync (RBA → CCURRA)

Automatically fetches daily SPOT exchange rates from the Reserve Bank of Australia (RBA) table F11.1
and writes them to `mvxcdta.CCURRA` in MOVEX.

**Why direct DB2 write?** M3 MI transaction `CRS055MI` does not support exchange rate updates.
Direct DB2 INSERT is the only mechanism. Governed by **ADR-008** (Architecture Team approval required
before production deployment — see [`ai/memory/08-governance-and-decisions.md`](ai/memory/08-governance-and-decisions.md)).

### Configuration (`appsettings.json`)

```json
"ExchangeRateSync": {
  "Enabled": true,
  "ScheduleTimeUtc": "23:00",
  "CheckIntervalMinutes": 60,
  "RbaUrl": "https://www.rba.gov.au/statistics/tables/csv/f11.1-data.csv",
  "TimeoutSeconds": 30,
  "SkipWeekends": true,
  "FallbackDays": 3,
  "Currencies": ["USD"]
}
```

All DB2 access reuses the existing `DataSources:Db2:ConnectionString` secret (user-secrets / Azure Key Vault).
No separate connection string is needed.

### Rate Convention

`1 USD = 0.6828 AUD` — rates express how many AUD equal 1 unit of the foreign currency (RBA convention).
Rate type 99 (SPOT) is used. Rates are INSERT-only — immutable once written for a given date.

### Weekend / Holiday Fallback

RBA does not publish on weekends or public holidays. The API automatically looks back up to
`FallbackDays` (default 3) days and returns the most recent available rate with `usedFallback: true`.

```bash
# Query SPOT rate for a weekday
curl -H "X-API-Key: <key>" https://srxwebapp1/reporting/api/v1/exchange-rates/USD/2026-03-19

# Query for Saturday — returns Friday rate with usedFallback=true
curl -H "X-API-Key: <key>" https://srxwebapp1/reporting/api/v1/exchange-rates/USD/2026-03-21
```

**Example response (weekend fallback):**
```json
{
  "currency": "USD",
  "requestedDate": "2026-03-21",
  "effectiveDate": "2026-03-20",
  "rate": 0.6828,
  "rateType": "SPOT",
  "source": "RBA",
  "usedFallback": true,
  "isWeekend": true,
  "lastSyncUtc": "2026-03-19T23:00:05Z"
}
```

### Same-Day Rate Not Yet Published (Expected Behaviour)

Querying today's rate **before ~4:00 PM AEST** will return the previous business day's rate
with `usedFallback: true`. This is normal and expected — not a bug.

**Why:** The RBA publishes its daily F11.1 CSV at approximately 4:00 PM AEST each business day.
The nightly sync runs at **23:00 UTC (~9:00 AM AEST next day)**, so the typical lag is:

| Time of query (AEST) | Rate returned |
|---|---|
| Before ~4:00 PM today | Previous business day's rate (`usedFallback: true`) |
| After ~4:00 PM today | Still previous day — sync hasn't run yet |
| After 23:00 UTC (~9:00 AM AEST tomorrow) | Today's rate (`usedFallback: false`) |

**Example:** Querying at 11:52 AM on a Monday will return Friday's rate — Monday's rate will not
be published by RBA until ~4 PM, and will not be written to CCURRA until the 23:00 UTC sync.

**To get today's rate immediately after 4 PM AEST:** Restart the Reporting Service — the startup
sync runs immediately and will pick up the newly published rate.

### IIS Deployment Note (Critical)

The nightly 23:00 UTC timer requires the IIS app pool idle timeout to be **disabled**:

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool `
    /apppool.name:"ReportingService" /processModel.idleTimeout:00:00:00
```

Without this, the default 20-minute idle timeout recycles the app pool before the timer fires.

### Pre-Production Checklist

- [ ] ADR-008 approved by Architecture Team
- [ ] DBA grants INSERT permission on `mvxcdta.CCURRA` for ODBC service account
- [ ] IT confirms outbound HTTPS to `rba.gov.au:443` from SRXWEBAPP1
- [ ] Secrets file configured on SRXWEBAPP1 (`scripts\Setup-ServerSecrets.ps1` — ADR-009)
- [ ] App pool idle timeout set to 0

---

## Known Issues & Workarounds

- **Crystal Reports inventory:** T0 pending — validate Iteration 1 report selection before Sprint 2
- **QuestPDF license:** IT/Legal review required before production deployment (T21a)
- **Azure Key Vault:** IT must confirm availability on SRXWEBAPP1 (T27)
- **Async polling:** Heavy Smartbook reports (>5 min) not yet supported — planned RSAI-2

---

## Audit Logging

Deferred to Iteration 2 (RSAI-2). Architecture Team formal approval pending.
See [ai/memory/08-governance-and-decisions.md](ai/memory/08-governance-and-decisions.md).

**Retention policy:** 7 years minimum (ISO 27001 / Malaysian tax law).

---

## Links

| Resource | Location |
|---|---|
| AI Operating Rules | [ai/rules.md](ai/rules.md) |
| System Architecture | [ai/memory/01-system-architecture.md](ai/memory/01-system-architecture.md) |
| Domain Model | [ai/memory/02-domain-model.md](ai/memory/02-domain-model.md) |
| Sprint Backlog | [ai/tasks/sprint-backlog.md](ai/tasks/sprint-backlog.md) |
| Decision Log | [ai/evidence/decision-log.md](ai/evidence/decision-log.md) |
| Known Risks | [ai/memory/06-known-risks-and-pitfalls.md](ai/memory/06-known-risks-and-pitfalls.md) |
| Setup Guide | [docs/SETUP.md](docs/SETUP.md) |
| Deployment Guide | [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) |
| UAT Deployment Runbook | [docs/runbooks/uat-deployment.md](docs/runbooks/uat-deployment.md) |
| Troubleshooting | [docs/runbooks/TROUBLESHOOTING.md](docs/runbooks/TROUBLESHOOTING.md) |
| WORKSPACE_RULES | [../WORKSPACE_RULES.md](../WORKSPACE_RULES.md) |
