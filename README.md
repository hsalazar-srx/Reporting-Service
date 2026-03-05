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
| Config | dotnet user-secrets (dev) / Azure Key Vault (prod) |

---

## Prerequisites

- .NET 8.0 SDK
- IBM i ODBC driver (iAccess / IBM i Access Client Solutions)
- Access to MOVEX AS/400 IBM i (DSN configured)
- Access to MovexDatawarehouse SQL Server (150.3.20.116)
- Azure Key Vault access (production only)

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

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for IIS deployment to SRXWEBAPP1.

**Deploy order:** Always deploy Reporting Service FIRST, then SM-Portal.

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
| Troubleshooting | [docs/runbooks/TROUBLESHOOTING.md](docs/runbooks/TROUBLESHOOTING.md) |
| WORKSPACE_RULES | [../WORKSPACE_RULES.md](../WORKSPACE_RULES.md) |
