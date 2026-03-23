# Reporting Service — Product Vision

**Version:** 1.0
**Date:** March 2026
**Status:** Active

---

## What This Service Does

The **Scanfil APAC Reporting Service** is an enterprise-grade REST API that replaces Crystal Reports
(including compound "Smartbook" subreports) across all 6 manufacturing reporting domains.

It delivers management reports as JSON (preview), Excel (ClosedXML), and PDF (QuestPDF) from:
- **IBM i AS/400 DB2** — MOVEX live operational data
- **MovexDatawarehouse** (SQL Server, 150.3.20.116) — pre-aggregated DIFOT and procurement data

Users access reports **via Scanfl APAC-Portal** — no new portal is deployed. SA-Portal is extended with
reporting pages and interactive charts (Recharts). The Reporting Service itself has no UI.

---

## Why We Are Building This

### Problem: Crystal Reports
- Hard to maintain — no version control, no unit tests, binary .rpt files
- Dependent on 87-step manual Excel processes for some reports (DIFOT)
- No self-service — every report change requires IT involvement
- Cannot deliver JSON for charting or API consumers
- Smartbook subreports are opaque black boxes — untestable

### Solution: Reporting Service
- Clean, composable C# pipeline (Fetch → Transform → Render)
- Smartbooks modelled as `ReportDataSet.SubReports[]` — unit-testable, composable
- Three output formats from one endpoint (JSON for charts, Excel for finance, PDF for distribution)
- Config-driven catalog (report-catalog.json) — new reports without code deployments
- SM-Portal integration — finance and operations teams get self-service access

---

## Target Users

| User Group | Domain | Primary Reports |
|---|---|---|
| Finance Controller / CFO | Cost Management, Finance | Average Cost Snapshot, WAC History, Trial Balance |
| COO / Supply Chain Manager | Supply Chain Performance | DIFOT Monthly, OTD Trend |
| Procurement Manager | Procurement | Open PO, Supplier OTIF, GRNI Aging |
| Operations / Warehouse Manager | Inventory Management | SOH Report, Slow/Dead Stock |
| Production Manager | Production | MO Status, Production vs Plan |
| IT / Developers | All | API JSON output for dashboards |

---

## Success Metrics (Phase 1 — RSAI-1)

- Finance can request Average Cost Snapshot as Excel or PDF for any as-at date
- WAC History trend shows FCAAVP data grouped by facility
- Cost Variance Analysis shows standard vs WAC difference per item per facility
- ≥80% unit test coverage on Core + Infrastructure
- Build time < 2 minutes
- Report execution < 60 seconds for Cost Management reports

## Success Metrics (Full Programme)

- All Crystal Reports replaced by RSAI-6 (18-month programme)
- 87-step Excel DIFOT process eliminated in RSAI-2
- ≥95% report execution success rate
- < 5-minute response for Smartbook reports (with async polling from RSAI-2+)

---

## Architecture Principle

The Reporting Service is a **pure data/rendering backend**. It has no UI.
SM-Portal owns:
- Authentication (Windows AD — existing)
- RBAC (existing, with reporting roles added in Iteration 3)
- Audit logging (JSONL — existing pattern)
- Navigation, interactive charts (Recharts), file downloads

This avoids code duplication and leverages SM-Portal's existing security infrastructure.

---

## Iteration Sequence

| Iteration | Domain | Key Deliverables |
|---|---|---|
| **RSAI-1** | **Cost Management** | Average Cost Snapshot, WAC History, Cost Variance Analysis |
| **RSAI-SM** | **SM-Portal UI** | ReportBrowserPage, ReportViewerPage, Recharts charts |
| RSAI-2 | Supply Chain Performance | DIFOT — replaces 87-step Excel process |
| RSAI-3 | Finance | AP/AR Ledger, Aging, Trial Balance; + full RBAC |
| RSAI-4 | Inventory Management | SOH, Slow/Dead Stock, Stock Movement |
| RSAI-5 | Procurement | Supplier OTIF, GRNI Aging, Spend Analysis |
| RSAI-6 | Production | MO Status, Production vs Plan, MO Costing |
