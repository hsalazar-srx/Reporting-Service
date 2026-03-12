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

- **RSAI-7:** Scheduled report delivery (email/batch) — Scheduler Service consuming `/execute`
  ⚠️ *Re-evaluate scope against Superset Celery alerts if RSAI-BI adopted (see ADR-009)*
- **RSAI-8:** Async report polling (`POST /execute` → 202 + jobId → `GET /jobs/{id}`)
- **RSAI-9:** Column-level encryption for sensitive report payloads (WORKSPACE_RULES Phase 2)
- **RSAI-10:** Azure SQL migration for audit logs (if JSONL proves insufficient)

---

## RSAI-BI: Self-Hosted BI Analytics Platform (PROPOSAL)

**Status:** ⏳ PROPOSAL — pending Architecture Team approval (ADR-009)
**Full proposal:** [ai/memory/09-bi-platform-proposal.md](./09-bi-platform-proposal.md)
**Platform:** Apache Superset (Docker) + SQL Server DataWarehouse semantic layer
**Goal:** Deliver Power BI-equivalent self-service analytics and embedded dashboards across all
6 manufacturing domains — complements the Reporting Service (not a replacement)

| Phase | Codename | Deliverables | Status |
|---|---|---|---|
| **BI-0** | **RSAI-BI-0** | Docker host provisioned; Superset deployed; SQL Server DW connected; connectivity validated | ⏳ Proposal |
| **BI-1** | **RSAI-BI-1** | Semantic layer foundation for Cost Management; 2–3 pilot dashboards; internal demo | ⏳ Proposal |
| **BI-2** | **RSAI-BI-2** | Guest token integration with SM-Portal; first embedded dashboard in production | ⏳ Proposal |
| **BI-3** | **RSAI-BI-3** | SQL Lab for power users; row-level security per domain; analyst training; QuestPDF fallback evaluated | ⏳ Proposal |
| **BI-4** | **RSAI-BI-4** | Dashboards across all 6 manufacturing domains; scheduled email delivery (Celery alerts) | ⏳ Proposal |
| **BI-5** | **RSAI-BI-5** | IBM i DB2 direct connectivity for near-real-time datasets; operational monitoring dashboards | ⏳ Proposal |
