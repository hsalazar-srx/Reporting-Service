# Known Risks & Pitfalls — Reporting Service

**Version:** 1.0
**Date:** March 2026
**Source:** Devil's advocate review against movex-rest-api, SM-Portal, MyInvois-Service implementations

---

## Critical

### Gap 1 — No rate limiting on ApiKeyMiddleware template
**Risk:** `ApiKeyAuthenticationMiddleware` (source template) has no throttling. Attacker with network
access can brute-force the API key.
**Mitigation:** Add ASP.NET Core `AddRateLimiter` (fixed window, 60 req/min per IP) in `Program.cs`.
**Task:** T4 — do NOT blindly copy template; add rate limiting as new functionality.

### Gap 2 — Timing attack on API key comparison
**Risk:** `string.Equals(key, configuredKey, StringComparison.Ordinal)` is variable-time.
**Mitigation:** Replace with `CryptographicOperations.FixedTimeEquals(...)`.
**Task:** T4 — one-line change, verified in test T9.

---

## High

### Gap 3 — Polly NOT in DirectQueryDataSource template
**Risk:** Plan assumes Polly is in the template. It is NOT. `DirectQueryDataSource.cs` only catches
`OdbcException` and rethrows — no retry, no circuit breaker.
**Mitigation:** `Db2DirectFetcher` implements Polly from scratch. See `ai/patterns/db2-odbc.md`.
**Task:** T12 — explicit Polly ResiliencePipeline implementation required.

### Gap 4 — RBAC deferred → reports unprotected until Iteration 3
**Risk:** All authenticated Windows AD users in SM-Portal can access all 6 report domains.
**Mitigation:** SM-Portal `ReportingController` proxy enforces AD group allowlist
(`config/reporting-access-config.json`) for Iterations 1-2. Full RBAC in Iteration 3.
**Task:** RSAI-SM.

### Gap 5 — Synchronous execution timeout for heavy Smartbook reports
**Risk:** `POST /execute` synchronous. Heavy reports may run 3-10 minutes. ASP.NET Core default = 30s.
**Mitigation:** Set `Report:ExecutionTimeoutSeconds` = 300 default (configurable per report).
Async polling (`POST /execute` → 202 + jobId) planned for Iteration 2.
**Task:** T7 (catalog), T8 (appsettings), ADR-003.

### Gap 6 — No last-good-config fallback in catalog provider template
**Risk:** `EndpointRegistryProvider` (source template) throws `InvalidDataException` on invalid JSON.
A bad catalog file deployment kills all reports.
**Mitigation:** `JsonReportCatalogProvider` adds `_lastKnownGoodCatalog` field. On reload failure:
log Error + retain last-good state + return `Degraded` in health check.
**Task:** T3.

---

## Medium

### Gap 7 — `cost.inventory-valuation-summary` violates domain boundaries
**Risk:** That report needs MITBAL (Iteration 4 table). Including it in Iteration 1 breaks domain isolation.
**Mitigation:** Replaced with `cost.cost-variance-analysis` (pure MITFAC — standard vs WAC).
MITBAL deferred to Iteration 4.
**Status:** RESOLVED in catalog.

### Gap 8 — No Crystal Reports inventory — iteration priorities are guesses
**Risk:** The 3 Iteration 1 reports were chosen without reference to actual Crystal Reports usage.
**Mitigation:** T0 — collect Crystal Reports inventory from stakeholders before Sprint 2 begins.
**Status:** PENDING — must be done before T14.

### Gap 9 — Subreport data snapshot inconsistency
**Risk:** Multi-subreport Smartbooks execute sequential fetches. DB2 data can change between fetches.
**Mitigation:** `ExecutedAtUtc` field in ReportDataSet makes snapshot time explicit.
Use `asAtDate` parameter consistently across all subreport fetches.
Document: "DB2 AS/400 ODBC does not support multi-statement transactions; data consistency
across SubReports is best-effort."
**Task:** T12a.

### Gap 10 — No standardized error response contract
**Risk:** SM-Portal `ReportingController` proxy needs a known error shape to render user messages.
**Mitigation:** `ErrorResponse` defined as: `{ code, message, correlationId, timestamp }`.
Error codes: `REPORT_NOT_FOUND`, `PARAMETER_VALIDATION_FAILED`, `DATA_SOURCE_UNAVAILABLE`,
`EXECUTION_TIMEOUT`, `UNAUTHORIZED`.
**Task:** T5.

### Gap 11 — QuestPDF Community license — commercial production use
**Risk:** Free for <$1M annual revenue. SRX may exceed. License changed from MIT in 2023.
**Mitigation:** IT/Legal must review before RSAI-1 production deployment.
Fallback: PdfSharp (MIT) or iTextSharp (AGPL/commercial).
**Task:** T21a gate — blocks T27 (production deployment).

---

## Low

### Gap 12 — SM-Portal and Reporting Service deployment coupling
**Risk:** Between deployments, `/reports` routes in SM-Portal return 502/503.
**Mitigation:** `ReportingServiceClient` catches connection-refused → 503 + `Retry-After: 60`.
Deploy order: Reporting Service FIRST, then SM-Portal — documented in DEPLOYMENT.md.

### Gap 13 — No API versioning
**Risk:** Breaking changes affect all callers simultaneously.
**Mitigation:** All routes use `/api/v1/` from day one. `X-Api-Version: 1.0` header on responses.
**Status:** RESOLVED in Sprint 1 tasks.

### Gap 14 — Report scheduling (batch/email) not in scope
**Risk:** Management may expect scheduled reports (daily cost snapshot email).
**Mitigation:** Not in RSAI-1 through RSAI-6. Document as Known Limitation.
Design: RSAI-7 or separate Scheduler Service consuming `/execute` endpoint.

### Gap 15 — `rbac-config.json` in Reporting Service would be misleading
**Risk:** Implies the Reporting Service enforces RBAC (it doesn't — API Key only).
**Mitigation:** RBAC lives only in `c:\Projects\SM-Portal\config\rbac-config.json`.
Reporting Service has only `config/report-catalog.json`.
**Status:** RESOLVED — removed from project structure.
