# Product Roadmap — Reporting Service

**Version:** 1.0
**Date:** March 2026

---

## Iteration Sequence (18-Month Crystal Reports Replacement)

| Iteration | Codename | Domain | Key Deliverables | Status |
|---|---|---|---|---|
| **1** | **RSAI-1** | **Cost Management** | Average Cost Snapshot, WAC History, Cost Variance Analysis; JSON + Excel + PDF; API Key auth; rate limiting; `/api/v1/` routes | 🔄 IN PROGRESS |
| **SM** | **RSAI-SM** | **SM-Portal UI** | `ReportingController` proxy; `ReportingServiceClient`; `ReportBrowserPage`, `ReportViewerPage`, Recharts charts | ⏳ After RSAI-1 |
| 2 | RSAI-2 | Supply Chain Performance | DIFOT Monthly, DIFOT by Customer, Late Order Backlog, OTD Trend — **replaces 87-step Excel process** | ⏳ Planned |
| 3 | RSAI-3 | Finance | AP/AR Ledger, Aging, GRNI Reconciliation, Trial Balance; **+ full RBAC roles** in SM-Portal | ⏳ Planned |
| 4 | RSAI-4 | Inventory Management | SOH Report, Slow/Dead Stock, Stock Movement, MRP Exceptions; MITBAL queries | ⏳ Planned |
| 5 | RSAI-5 | Procurement | Open PO, Supplier OTIF, Purchase Savings KPI, GRNI Aging, Spend Analysis | ⏳ Planned |
| 6 | RSAI-6 | Production | MO Status, Production vs Plan, Material Consumption Variance, MO Costing | ⏳ Planned |

---

## RSAI-SM Can Run in Parallel with RSAI-2

Backend team delivers domain data. Frontend team extends SM-Portal.
Both tracks independent after RSAI-1's `/preview` endpoint is available.

---

## Known Future Work (Beyond RSAI-6)

- **RSAI-NL:** Natural language query layer — Claude API translates plain-English questions to
  parameterised DB2 SQL, executed through the existing pipeline. Ad-hoc Finance/Operations queries
  without developer involvement. See [`ai/memory/09-rsai-nl-iteration.md`](09-rsai-nl-iteration.md)
  for full sketch: architecture, safety validator design, risks, governance gates, ~16-day estimate.
  **Prerequisite spike:** ½-day LLM accuracy test against DB2 (CONO=300) before scheduling.
- **RSAI-7:** Scheduled report delivery (email/batch) — Scheduler Service consuming `/execute`
- **RSAI-8:** Async report polling (`POST /execute` → 202 + jobId → `GET /jobs/{id}`)
- **RSAI-9:** Column-level encryption for sensitive report payloads (WORKSPACE_RULES Phase 2)
- **RSAI-10:** Azure SQL migration for audit logs (if JSONL proves insufficient)
