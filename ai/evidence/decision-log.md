# Decision Log — Reporting Service

**Format:** ADR (Architecture Decision Record)

---

## ADR-001: Audit Logging Deferred to Iteration 2

- **Date:** March 2026
- **Context:** WORKSPACE_RULES.md mandates SQL Server for audit logs. Service is read-only.
- **Decision:** Defer audit to Iteration 2 using JSONL. `IAuditService` abstracts storage.
- **Rationale:** No idempotency or operational audit queries needed for read-only service.
- **Consequences:** Audit entries are `report-executed` events only.
- **Reversibility:** Yes — swap `IAuditService` implementation in Iteration 2 or 3.
- **Approval required:** Yes — Architecture Team (pending, required before T27)

## ADR-002: API Versioning from Day One

- **Date:** March 2026
- **Context:** Breaking changes will occur as domains are added.
- **Decision:** All routes use `/api/v1/` prefix. `X-Api-Version: 1.0` response header.
- **Rationale:** Zero cost at start; prevents forced migration of all callers on breaking changes.
- **Reversibility:** Not applicable — additive change.

## ADR-003: Synchronous Execution (Iteration 1)

- **Date:** March 2026
- **Context:** Cost Management reports expected < 60s. Complex Smartbooks may exceed 5 min.
- **Decision:** Synchronous with 300s timeout (configurable per report).
- **Limitation:** Async polling planned for RSAI-2 (// TODO RSAI-2: async job polling).
- **Reversibility:** Yes — add async layer in RSAI-2 without breaking existing API.

## ADR-004: Interim RBAC via AD Group Allowlist

- **Date:** March 2026
- **Context:** Full RBAC deferred to Iteration 3. Reports must not be open to all AD users.
- **Decision:** SM-Portal checks AD group allowlist (`config/reporting-access-config.json`).
- **Consequences:** Reporting team must maintain allowlist until RSAI-3.
- **Reversibility:** Yes — replace allowlist check with RbacMiddleware in RSAI-3.

## ADR-005: QuestPDF for PDF Generation

- **Date:** March 2026
- **Context:** Need composable PDF generation for Smartbook sections.
- **Decision:** QuestPDF Community Edition.
- **Risk:** Commercial license may be required (see Gap 11). Fallback: PdfSharp.
- **Approval required:** IT/Legal license review before production deployment (T21a).

## ADR-006: Azure Key Vault for Production Secrets

- **Date:** March 2026
- **Context:** WORKSPACE_RULES.md mandates Azure Key Vault for production. Not IIS env vars.
- **Decision:** Azure Key Vault for all production secrets.
- **Risk:** Azure KV may not be provisioned on SRXWEBAPP1.
- **Fallback:** Infrastructure Exception with IT if unavailable.

## ADR-007: Polly Implemented from Scratch in Db2DirectFetcher

- **Date:** March 2026
- **Context:** `DirectQueryDataSource` template has no Polly. Plan incorrectly assumed it did.
- **Decision:** `Db2DirectFetcher` implements Polly `ResiliencePipeline` independently.
- **Policy:** Retry(3, exponential) + CircuitBreaker(5 failures → 30s open).
- **Reversibility:** N/A — this is the correct approach.

## 2026-10-23: Supply Chain domain and per-department report roles

- **Date:** 23 October 2026
- **Context:** Ruwani Thilanka (Supply Chain) needs POs issued per buyer; the finance reports must stay
  hidden from her and hers from finance. Both existing reports require the single role `Reporting_Read`.
- **Decision:** New domain `SupplyChain` with report `supplychain.po-issued-by-buyer` (MPHEAD), requiring
  `Reporting_SupplyChain_Management`. Roles follow the vault's ADR-061: one report-audience Application
  Role per department and level (`rpt-<dept>`, `rpt-<dept>-mgmt`), with Business Function groups (`grp-*`)
  nested into them in AD, and `rpt-admin` for support. Enforcement stays in SM-Portal (`ReportAccess`).
- **Consequences:** SM-Portal's `config/rbac-config.json` needs the new role before anyone can see the
  report (deny by default). The two existing reports move off `Reporting_Read` later
  (`Reporting_General`, `Reporting_Finance_Management`). This delivers the per-domain roles ADR-004
  deferred to "Iteration 3", without new middleware.
- **Reversibility:** Yes — a role name in the catalog and a config entry.

## ADR-008: Rename Namespaces from SrxReporting.* to Reporting.*

- **Date:** March 2026
- **Context:** Stakeholder preference for cleaner naming.
- **Decision:** All assemblies, namespaces use `Reporting.*` prefix.
- **Approved by:** Stakeholder (verbal — March 2026).

## ADR-011: Catalog Reload Tests Trigger the Reload Explicitly

- **Date:** 2026-11-03
- **Context:** `BackgroundReload_OnFailure_ServesLastKnownGoodCatalog` and `BackgroundReload_OnSuccess_ClearsLastKnownGoodFlag` failed intermittently in full-suite runs (3 of 5 runs on master 28a12f4). They used a real 50 ms `Timer` and asserted after `Task.Delay(300/500)`. With xUnit running test classes in parallel, thread-pool pressure delays the timer callback past the wait. The timer's reads (`FileShare.Read`) also race with the test's `File.WriteAllText`.
- **Decision:** `JsonReportCatalogProvider.ReloadInBackgroundAsync()` changed from `private` to `internal` (visible to `Reporting.Tests` through the existing `InternalsVisibleTo`). The tests create the provider with `Timeout.InfiniteTimeSpan` so the timer never fires, then call and await the reload directly. The `[Collection("CatalogTests")]` attribute, added earlier to work around the timer race, is removed.
- **Rejected:** Injecting `TimeProvider`. `FakeTimeProvider.Advance` fires the callback, but the reload is fire-and-forget async file I/O, so the tests would still need to wait for it. It would add a package and a constructor parameter without removing the wait.
- **Consequences:** No production behaviour change: same constructor, timer and reload logic. The tests no longer cover the timer calling the reload; that is one line of constructor wiring.
- **Verification:** Full suite (`dotnet test`) run 12 times in a row, 225/225 passed each time.
- **Reversibility:** Yes. Revert the visibility change and the test edits.
