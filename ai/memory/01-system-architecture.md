# System Architecture & ADRs — Reporting Service

**Version:** 1.0
**Date:** March 2026

---

## Architecture Overview

Three-assembly clean architecture:

| Assembly | Purpose | Dependencies |
|---|---|---|
| `Reporting.Core` | Pure C# domain models and interfaces — no framework dependencies | None |
| `Reporting.Infrastructure` | Dapper, ClosedXML, QuestPDF, ODBC, Polly implementations | Reporting.Core |
| `Reporting.Api` | ASP.NET Core 8 host, controllers, middleware | Reporting.Core, Reporting.Infrastructure |
| `Reporting.Tests` | xUnit + Moq + FluentAssertions | Reporting.Core, Reporting.Infrastructure |

## Report Pipeline

```
Catalog Lookup → Parameter Validation → Data Fetch → Transform → Render → HTTP Response
```

- `IReportCatalogProvider` — loads and validates report-catalog.json
- `IParameterValidator` — validates typed parameters (date, string, multiselect)
- `IDataFetcher` — fetches raw data from DB2 or SQL Server
- `ITransformer` — groups, sorts, calculates subtotals, flags
- `IRenderer` — converts ReportDataSet to JSON | Excel | PDF bytes
- `ReportPipelineService` — orchestrates all stages

## Data Sources

| Source | Type | Use |
|---|---|---|
| IBM i AS/400 DB2 | ODBC (positional params `?`) | MOVEX live data — MITFAC, FCAAVP, MITMAS, etc. |
| MovexDatawarehouse | SQL Server (named params `@`) | Pre-aggregated DIFOT, procurement data |
| MOVEX REST API | HTTP (future) | MI-transaction lookups — RSAI-3+ |

---

## Architecture Decision Records

### ADR-001: Audit Logging Deferred to Iteration 2

**Date:** March 2026
**Status:** Pending Architecture Team approval (required per WORKSPACE_RULES.md)

**Context:** WORKSPACE_RULES.md mandates SQL Server for all audit logs. This service is read-only —
no transactional data requiring idempotency or audit queries.

**Decision:** Defer audit logging to Iteration 2. Use JSONL files (same as SM-Portal pattern) when
introduced. `IAuditService` interface abstracts storage — SQL Server can be swapped in transparently.

**Consequences:** Audit entries are `report-executed` events only. No operational process queries
them back in Iterations 1-2.

**Approval Required:** Architecture Team must sign off before production deployment (T27a).
**Approver:** _______________ **Date:** _______________

---

### ADR-002: API Versioning from Day One

**Date:** March 2026
**Status:** Approved (implemented in Sprint 1)

**Decision:** All routes use `/api/v1/` prefix. Response header `X-Api-Version: 1.0` on all responses.

**Rationale:** Zero cost at start; breaking changes don't affect all callers simultaneously.

---

### ADR-003: Synchronous Report Execution (Iteration 1)

**Date:** March 2026
**Status:** Approved with known limitation

**Decision:** `POST /api/v1/reports/{id}/execute` is synchronous with configurable timeout
(default 300s, configurable per report in catalog).

**Limitation:** Smartbook reports with 6+ subreports may run 3-10 minutes. Async polling
(`POST /execute` → 202 + jobId → `GET /jobs/{id}`) is **planned for Iteration 2**.

**Mitigation:** Cost Management reports expected < 60s based on existing SQL benchmarks.

---

### ADR-004: Interim RBAC via AD Group Allowlist

**Date:** March 2026
**Status:** Approved for Iterations 1-2

**Decision:** SM-Portal `ReportingController` proxy enforces a simple AD group allowlist
(`config/reporting-access-config.json`) until full RBAC lands in Iteration 3.

**Full RBAC:** Per-domain roles (`Reporting_CostManagement_Read`, etc.) enforced by SM-Portal
`RbacMiddleware` in Iteration 3.

---

### ADR-005: QuestPDF for PDF Generation

**Date:** March 2026
**Status:** Development approved; **Production pending IT/Legal license review (T21a)**

**Decision:** QuestPDF Community Edition for PDF generation.

**Risk:** Community Edition free for projects with <$1M annual revenue. SRX may exceed this.
**Fallback:** PdfSharp (MIT) if QuestPDF is disqualified by IT/Legal.

**Gate:** IT/Legal must sign off before T27 (production deployment).
**Approver:** _______________ **Date:** _______________

---

### ADR-006: Azure Key Vault for Production Secrets

**Date:** March 2026
**Status:** Approved (pending infrastructure confirmation)

**Decision:** All production secrets (API keys, connection strings) stored in Azure Key Vault.
Dev: `dotnet user-secrets`. Never IIS environment variables.

**Risk:** Azure Key Vault may not be provisioned on SRXWEBAPP1. IT must confirm.
**Fallback:** If unavailable, file Infrastructure Exception with IT before T27.

---

### ADR-007: Db2DirectFetcher — Polly from Scratch

**Date:** March 2026
**Status:** Approved

**Decision:** `Db2DirectFetcher` implements Polly `ResiliencePipeline` independently (not inherited
from `DirectQueryDataSource` template, which has no Polly).

**Policy:** Retry(3, exponential: 1s, 2s, 4s) + CircuitBreaker(5 failures → 30s open).
