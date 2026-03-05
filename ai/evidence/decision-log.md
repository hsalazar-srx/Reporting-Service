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

## ADR-008: Rename Namespaces from SrxReporting.* to Reporting.*

- **Date:** March 2026
- **Context:** Stakeholder preference for cleaner naming.
- **Decision:** All assemblies, namespaces use `Reporting.*` prefix.
- **Approved by:** Stakeholder (verbal — March 2026).
