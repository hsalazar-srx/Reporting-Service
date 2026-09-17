# Shelved — Inventory List (`.dqy` replacement)

**Status:** Designed 2026-09-01, build not started. Deferred pending **ADR-028**.

## What it would add here

A fourth catalog report, `inventory.item-list`, and the **first `InventoryManagement` domain** report
(existing three are all CostManagement):

- `config/report-catalog.json` — new entry; params `companyCode` / `excludeStatus` / `itemGroup`;
  formats `json` + `excel` (no PDF — 5 columns × tens of thousands of rows is not a usable PDF, and
  it avoids the unresolved QuestPDF licence gate T21a)
- `src/Reporting.Infrastructure/Domains/InventoryManagement/ItemListFetcher.cs` — extends the
  existing `Db2DirectFetcher` (Polly, ODBC, `ExecutedAtUtc` all inherited); mirror
  `AverageCostFetcher.cs` including its `BuildDataSet` helper
- One `AddSingleton<IDataFetcher>` line in `Program.cs`

**No transformer needed** — `CostManagementTransformer` registers `ReportId => "*"` and is a
pass-through, which `ReportPipelineService` already falls back to.

Column aliases are the **verbatim legacy headers** (`"Item number"`, `"Item group Description"`, …)
so the output workbook is drop-in familiar to the user of the retired `.dqy`.

## Why it is shelved

The design is **Path A** work. [ADR-028](../../../Knowledge-Management/vault/decisions/reporting-service-architecture-path-is-under-review-three-options-evaluated.md)
states plainly: *"Until these answers are in hand, no development effort should be committed to any
path."* Five questions remain open, and a fourth path (Apache Superset) was added 2026-03-11.

The **SQL survives any path** — it is reusable by Power BI or Superset over ODBC. Only the fetcher
class and catalog entry are Path A-specific.

## Full design

`c:/Projects/Knowledge-Management/vault/projects/inventory-list-dqy-replacement.md`

Contains: TLS root-cause analysis, M3 field mapping verified against the schema dictionary, the
complete replacement SQL, two known row-count deltas vs. the legacy query (CONO filter, LEFT JOIN),
and an outstanding unverified assumption on `MMSTAT` semantics.
