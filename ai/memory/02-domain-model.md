# Domain Model — Reporting Service

**Version:** 1.0
**Date:** March 2026

---

## Six Manufacturing Reporting Domains

Named for business outcomes, not software layers.

| # | Domain | Namespace | Executive Owner | Primary MOVEX Tables | Iteration |
|---|---|---|---|---|---|
| 1 | **Cost Management** | `Reporting.Core.Domains.CostManagement` | CFO / Finance Controller | MITFAC, FCAAVP, MITMAS | **1** |
| 2 | **Supply Chain Performance** | `Reporting.Core.Domains.SupplyChainPerformance` | COO / Supply Chain Mgr | OOHEAD, OOLINE, ODHEAD, ODLINE, OCUSMA | 2 |
| 3 | **Finance** | `Reporting.Core.Domains.Finance` | CFO / Finance Manager | FPLEDG, FSLEDG, FGLEDG, FGRECL, CRACTR | 3 |
| 4 | **Inventory Management** | `Reporting.Core.Domains.InventoryManagement` | Operations / Warehouse Mgr | MITBAL, MITFAC, MITLOC, MITTRA | 4 |
| 5 | **Procurement** | `Reporting.Core.Domains.Procurement` | Procurement Manager | MPHEAD, MPLINE, FGRECL, MACOPU, CIDMAS | 5 |
| 6 | **Production** | `Reporting.Core.Domains.Production` | Operations / Production Mgr | MWOHED, MMOMAT, MWOOPE, BPMOPO | 6 |

---

## Domain Scope Boundaries (No Overlap)

- **Cost Management** = "what does each item cost?" — WAC, standard cost, variances — MITFAC cost fields
- **Finance** = "what did we record to the books?" — ledger transactions, AP/AR, GL
- **Inventory Management** = "what stock do we have?" — SOH quantities, locations, aging — MITBAL
- **Supply Chain Performance** = outbound delivery performance to customers (DIFOT, OTD)
- **Procurement** = inbound supplier performance and purchase efficiency
- **Production** = manufacturing order execution and efficiency

---

## Shared Infrastructure (Cross-Domain)

Never duplicated — always use shared repositories:

| Repository | Shared By | Table(s) |
|---|---|---|
| `ItemMasterRepository` | All 6 domains | MITMAS |
| `FacilityItemRepository` | Cost Management, Inventory, Production | MITFAC |
| `CustomerMasterRepository` | Supply Chain, Finance | OCUSMA |
| `VendorMasterRepository` | Finance, Procurement | CIDMAS |
| `GoodsReceiptRepository` | Finance (GRNI), Procurement | FGRECL |
| `MovexDateConverter` | All 6 domains | Utility — `INT YYYYMMDD` ↔ `DateTime` |

---

## Iteration 1 — Cost Management Detail

### MITFAC Key Fields (Item-Facility cost data)

| Field | Type | Description |
|---|---|---|
| CFFACI | CHAR | Facility (e.g., "001") |
| CFITNO | CHAR | Item number |
| CFACSO | DECIMAL | Standard cost |
| CFUCOS | DECIMAL | Unit cost (WAC) |
| CFSTQT | DECIMAL | Stock quantity (⚠️ Iteration 4 — do NOT use in Cost Management) |

### FCAAVP Key Fields (WAC history)

| Field | Type | Description |
|---|---|---|
| AVFACI | CHAR | Facility |
| AVITNO | CHAR | Item number |
| AVTRDT | INT | Transaction date (YYYYMMDD) |
| AVTRUS | DECIMAL | Unit cost after transaction |

### MITMAS Key Fields (Item master)

| Field | Type | Description |
|---|---|---|
| MMITNO | CHAR | Item number |
| MMITDS | CHAR | Item description |
| MMITGR | CHAR | Item group |
| MMSTAT | CHAR | Status (20=active, 90=inactive) |

---

## MOVEX Schema Convention

- Schema name: `mvxcdta` (configurable per company)
- Company mapping: `CMP100` → `mvxcdta` (default)
- Always use `{schema}.{table}` prefix in queries
- Schema configured in `appsettings.json` under `DataSources.Db2.Schema`
