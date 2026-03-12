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

## ADR-009: Apache Superset as Self-Hosted BI Analytics Platform (PROPOSAL)

- **Date:** March 6, 2026
- **Status:** ⏳ PROPOSAL — requires Architecture Team and stakeholder approval
- **Context:** Crystal Reports replacement (RSAI-1 to RSAI-6) addresses paginated/operational
  reporting via the .NET Reporting Service. It does not address interactive dashboards,
  self-service analytics, or Power BI-equivalent functionality. Analysts will remain on manual
  Excel processes unless an analytics platform is adopted.
- **Decision (proposed):** Deploy Apache Superset (Docker) as the self-hosted BI/analytics layer,
  with SQL Server DataWarehouse as the primary data source and IBM i DB2 as secondary.
  The Reporting Service remains the operational/paginated report engine. Superset adds the
  interactive dashboard, semantic layer, and self-service analytics layer.
- **Alternatives considered:**
  - SSRS — dismissed: legacy Microsoft product, no analytics capability, maintenance-mode roadmap
  - JasperReports CE — dismissed: declining OSS community, Java runtime, no analytics
  - Metabase OSS — dismissed: weaker semantic layer, AGPL implications, will be outgrown
  - Grafana OSS — dismissed: time-series/monitoring DNA, weak tabular/manufacturing reports
- **Rationale:**
  - Superset is Apache-governed (no license-change risk)
  - Native semantic layer (virtual datasets + metrics) replaces Power BI data model
  - Guest token API enables SM-Portal dashboard embedding without separate BI portal
  - SQLAlchemy connectivity future-proofs against data platform changes
  - Row-level security isolates manufacturing domain data natively
- **Constraints:**
  - Requires Docker infrastructure (approved by stakeholder)
  - ~~Requires new hire: BI/Data Engineer (Python)~~ — **✅ Resolved March 6, 2026. Position confirmed.**
  - Azure AD OAuth2 integration required before broad rollout (can use local auth for pilot)
  - SQL Server DW must be the primary semantic source; DB2 direct is secondary
- **Consequences:**
  - QuestPDF license risk (T21a / ADR-005) may be resolved by delegating PDF export to
    Superset's headless-Chrome export API — evaluate at RSAI-BI-3
  - RSAI-7 Scheduler Service scope should be re-evaluated against Superset's built-in
    Celery-based alert/report delivery
- **Full documentation:** [ai/memory/09-bi-platform-proposal.md](../memory/09-bi-platform-proposal.md)
- **Approval required:** Architecture Team before RSAI-BI-0 infrastructure
  - ~~People/HR (BI Engineer headcount)~~ — **✅ Resolved**
