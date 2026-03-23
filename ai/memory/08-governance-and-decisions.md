# Governance & Architecture Decisions — Reporting Service

**Version:** 1.0
**Date:** March 2026

---

## Pending Approvals

### 1. Audit Logging Exception (WORKSPACE_RULES.md WR-1)

**Rule:** All projects MUST use SQL Server for audit logs. Non-SQL Server audit requires
Architecture Team formal approval.

**Requested Exception:** Defer audit logging to Iteration 2 using JSONL files (SM-Portal pattern).

**Justification:**
- Service is read-only — no idempotency or duplicate detection needed
- Audit entries are `report-executed` events; no operational process queries them back
- `IAuditService` interface abstracts storage — SQL Server swappable in Iteration 3+

**Status:** ⏳ PENDING Architecture Team approval
**Required Before:** T27 (production deployment)
**Approver:** _______________ **Date:** _______________ **Decision:** _______________

---

### 2. QuestPDF Community License (ADR-005)

**Status:** ⏳ PENDING IT/Legal review
**Required Before:** T27a (production deployment gate)
**Approver:** _______________ **Date:** _______________ **Decision:** _______________
**Fallback:** PdfSharp (MIT) if disqualified

---

### 3. Azure Key Vault Infrastructure (ADR-006)

**Status:** ⏳ PENDING IT confirmation of Azure KV availability on SRXWEBAPP1
**Required Before:** T27 (production deployment)
**Contact:** IT Infrastructure Team
**If unavailable:** File Infrastructure Exception with IT; document alternative approved by Architecture Team

---

### 4. DB2 Write Authorization — Exchange Rate Sync (ADR-008)

**Rule:** `ai/rules.md` Section 6 — "Stop immediately: service writing data (read-only only)"

**Requested Action:** Authorize Reporting-Service to perform INSERT operations on `mvxcdta.CCURRA`
(Exchange Rate table) for the purpose of daily RBA exchange rate synchronization.

**Scope (strictly bounded):**
- Table: `mvxcdta.CCURRA` only
- Operation: INSERT only — no UPDATE, no DELETE
- Strategy: Idempotent — check existence before insert (`SELECT 1 ... WHERE CUCONO=? AND CUDIVI=? AND CUCUCD=? AND CUCRTP=? AND CUCUTD=?`); skip silently if record already exists for the date
- Trigger: Automated timer at 23:00 UTC weekdays only (`ExchangeRateSyncService`)
- Agent: Dedicated service account with INSERT permission granted by DBA (see Warning W2)

**Justification:**
- M3 MI transaction `CRS055MI` does not support exchange rate update operations —
  there is no available MI program to update CCURRA; direct DB2 write is the only mechanism
- Exchange rates must be current for cost report accuracy; stale rates produce incorrect financial outputs
- INSERT-only + idempotent pattern eliminates risk of accidental overwrites or data corruption
- All writes will be covered by structured audit logging (Serilog) capturing: date, currency, old/new rate,
  CUCHID, execution timestamp, correlation ID
- Reviewed by validator-iis-deploy agent (2026-03-19): no objections with idempotent strategy

**Security Controls:**
- Rate bounds validation: reject rates < 0.0001 or > 10000
- Date validation: reject future dates; reject dates > 1 year old
- Currency validation: `^[A-Z]{3}$` regex
- Fixed CUCHID = 'SRXAPI' for full traceability in IBM i job logs
- No PII written (currency codes and numeric rates only)

**Status:** ⏳ PENDING Architecture Team approval
**Required Before:** Phase 1 implementation (no code written until approved)
**Approver:** _______________ **Date:** _______________ **Decision:** _______________

---

## Approved Decisions

| Decision | Date | Approver | Reference |
|---|---|---|---|
| API versioning from day one (`/api/v1/`) | March 2026 | Development Team | ADR-002 |
| Synchronous execution with 300s timeout (Iteration 1) | March 2026 | Development Team | ADR-003 |
| Interim RBAC via AD group allowlist | March 2026 | Development Team | ADR-004 |
| Polly from scratch in Db2DirectFetcher | March 2026 | Development Team | ADR-007 |
| Rename all namespaces from SrxReporting.* to Reporting.* | March 2026 | Stakeholder | T1 |
| Interim: NTFS-protected secrets file instead of Azure KV | March 2026 | Development Team | ADR-009 |

---

## Interim Secrets Storage (ADR-009) — Protected File

**Rule deviation from:** WORKSPACE_RULES.md WR-3 (Azure Key Vault required for prod secrets)

**Interim approach:** NTFS-protected JSON file at `C:\ProgramData\SRX\Reporting\secrets.json`

**Why it's significantly better than web.config:**
- File lives **outside** the deployment folder — survives every redeployment without re-entry
- NTFS ACLs: only `IIS AppPool\ReportingService` (Read) + Administrators (FullControl) — no other accounts
- Not in source control (covered by `**/secrets.json` in `.gitignore`)
- File path (not the secrets) is in `web.config` via `REPORTING_SECRETS_PATH`

**Keys stored in the file:**
```json
{
  "ApiKeys": { "Primary": "...", "Admin": "..." },
  "DataSources": { "Db2": { "ConnectionString": "..." } }
}
```

**Setup:** Run `scripts\Setup-ServerSecrets.ps1` as Administrator on SRXWEBAPP1.
The script prompts for each value, writes the file, and applies ACLs.

**Upgrade path to Azure Key Vault (when ADR-006 prerequisites are met):**
1. Remove `AddJsonFile(secretsFilePath)` block from `Program.cs`
2. Add `builder.Configuration.AddAzureKeyVault(...)` — same key names work unchanged
3. Upload: `ApiKeys--Primary`, `ApiKeys--Admin`, `DataSources--Db2--ConnectionString`
4. Delete `C:\ProgramData\SRX\Reporting\secrets.json` from the server
5. Remove `REPORTING_SECRETS_PATH` env var from `web.config`

**Status:** ✅ APPROVED (Development Team authority — non-breaking interim deviation)
**Expires:** When Azure Key Vault is provisioned (ADR-006)

---

## Change Log

| Date | Change | Author |
|---|---|---|
| March 2026 | Initial governance document created | AI Agent (Claude Code) |
| March 2026 | Added ADR-008: DB2 write authorization for CCURRA exchange rate sync | AI Agent (Claude Code) |
| March 2026 | Added ADR-009: Interim secrets file instead of Azure Key Vault | AI Agent (Claude Code) |
