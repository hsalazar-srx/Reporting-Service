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

## 2026-11-03: Supply Chain buyer view — MSP file sync and its own SQLite store

- **Date:** 3 November 2026
- **Context:** Purchasing (Ruwani Thilanka, meeting 2026-10-30) want Kar Mei's weekly "Single Real-Time
  Weekly MSP Report" Excel file replaced by a page where each buyer updates only their own lines and the
  MSP columns refresh themselves. The legacy PHP MSP on SRXAPP03 stays and writes a report file every
  weekday (Output folder, since 2017). Decisions by Hector Salazar; design note in the Knowledge vault
  (`reviews/2026-11-03-supply-chain-buyer-view-design-note.md`).
- **Decision:**
  1. A self-contained module, `Domains/SupplyChain/BuyerView` (with `Domains/SupplyChain/MspOutput`), in
     Reporting-Service, not a new service: it reuses the data sources the service already has, and is
     removed whole when Oskar's Purchase Planners Workbench takes over. SM-Portal stays the gateway
     (sign-in, `pur-buyer` / `pur-manager`, pages), with no storage of its own.
  2. Storage: one SQLite file (`BuyerViewSync:DatabasePath`, `E:\data\supplychain.db` on SRXWEBAPP1),
     `Microsoft.Data.Sqlite` 8.0.31 pinned, Dapper. This is the **only** thing Reporting-Service writes;
     its sources (MOVEX, DBSRV, the MSP Output folder) stay read-only. Not MyInvois's `audit.db`.
  3. The MSP reader finds columns by header text, never position (18 columns before hotfix 1, 20 after,
     23 after hotfix 2). MSP's `//0` is an empty MOVEX date.
  4. Line key = MO + item + occurrence. The PO line is a field, not part of the key, so raising a PO
     keeps the line and its buyer entries. Tested on the real D100 files of 29 Oct – 3 Nov 2026: day-to-day
     churn 10–130 lines instead of 80–170 with the PO line in the key.
  5. Sync: the newest daily D100 file (customer runs skipped), applied in one transaction; lines missing
     from the file are closed, never deleted; buyer entries are never touched; a file with under half the
     previous row count is refused. Timer every 15 min, 05:45–09:30 Melbourne time, plus
     `POST /api/v1/supplychain/buyer-view/sync` for a Windows scheduled task (503 when disabled or failed).
     **Off by default** (`BuyerViewSync:Enabled=false`).
  6. Share access: the `ReportingAPI` app pool runs as ApplicationPoolIdentity with loadUserProfile=false,
     and SRXAPP03 is in the untrusted CORP domain, so the service connects to `\\SRXAPP03\MSPOutput$` as
     `SRXAPP03\svc-msp-read` itself (`NetworkShareConnection`, `WNetAddConnection2`) for each sync.
     Credentials in the NTFS secrets file (ADR-009); `Setup-ServerSecrets.ps1` asks for them (optional).
     Rejected: a named app pool identity + cmdkey, which would change how the whole service runs.
- **Consequences:** Reporting-Service is no longer purely stateless: it owns one SQLite file, which must be
  on a backed-up path (confirmation requested from Manal) with a nightly copy. The edit API and SM-Portal
  pages follow after purchasing validate the scope (2026-11-04).
- **Reversibility:** Yes — the module, its settings and its file can be removed without touching reports.
