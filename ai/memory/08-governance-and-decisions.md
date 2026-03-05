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

## Approved Decisions

| Decision | Date | Approver | Reference |
|---|---|---|---|
| API versioning from day one (`/api/v1/`) | March 2026 | Development Team | ADR-002 |
| Synchronous execution with 300s timeout (Iteration 1) | March 2026 | Development Team | ADR-003 |
| Interim RBAC via AD group allowlist | March 2026 | Development Team | ADR-004 |
| Polly from scratch in Db2DirectFetcher | March 2026 | Development Team | ADR-007 |
| Rename all namespaces from SrxReporting.* to Reporting.* | March 2026 | Stakeholder | T1 |

---

## Change Log

| Date | Change | Author |
|---|---|---|
| March 2026 | Initial governance document created | AI Agent (Claude Code) |
