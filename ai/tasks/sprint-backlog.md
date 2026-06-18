# Sprint Backlog — RSAI-1

**Sprint 1 — Foundation Skeleton**
**Goal:** Deployable service with catalog browsing and health checks. No reports executed yet.

---

## Sprint 1 Tasks

| Task | Description | Status |
|---|---|---|
| T0 | Collect Crystal Reports inventory from stakeholders | ⏳ PENDING (before Sprint 2) |
| T1 | Solution scaffold + full ai/ workspace | ✅ DONE |
| T2 | MAS updates: expert-reporting-analytics agent + 4 new skills | ✅ DONE |
| T3 | JsonReportCatalogProvider (with last-good-config fallback) | ✅ DONE |
| T4 | ApiKeyMiddleware (FixedTimeEquals + rate limiting) | ✅ DONE |
| T5 | ReportController GET /api/v1/reports (versioned) + ErrorResponse | ✅ DONE |
| T5b | Swashbuckle/OpenAPI (disabled in prod) | ✅ DONE |
| T6 | HealthController GET /api/v1/health + /data-sources | ✅ DONE |
| T7 | config/report-catalog.json — 3 Cost Management entries | ✅ DONE |
| T8 | appsettings.json — all config sections | ✅ DONE |
| T9 | Tests: CatalogProvider, ParameterValidator, ApiKeyMiddleware | ✅ DONE |

**Sprint 1 Done When:**
`GET /api/v1/reports?domain=CostManagement` returns 3 validated report definitions;
rate limit returns 429 after 60 req/min; invalid catalog retains last-good-config.

---

## Sprint 2 Tasks — DB2 Data Pipeline + JSON Output

| Task | Description | Status |
|---|---|---|
| T10 | Core interfaces: IDataFetcher, ITransformer, IRenderer, ReportDataSet, ReportOutput | ✅ DONE |
| T11 | ReportPipelineService — orchestrates all pipeline stages | ✅ DONE |
| T12 | Db2DirectFetcher base — ODBC positional params, Polly from scratch | ✅ DONE |
| T12a | ReportDataSet.ExecutedAtUtc — stamped at fetch start | ✅ DONE |
| T13 | MovexDateConverter — YYYYMMDD INT ↔ DateTime | ✅ DONE |
| T14 | AverageCostFetcher — MITFAC + MITMAS SQL (parameterised) | ✅ DONE |
| T15 | WacHistoryFetcher — FCAAVP CTE SQL (parameterised) | ✅ DONE |
| T16 | CostManagementTransformer — group by Facility → ItemType → Item | ✅ DONE |
| T17 | JsonRenderer — serialise ReportDataSet with SubReports support | ✅ DONE |
| T18 | POST /api/v1/reports/{id}/execute wired for JSON format | ✅ DONE |
| T19 | Tests: MovexDateConverter, ReportPipelineService (107 tests total, 107 passing) | ✅ DONE |

---

## Sprint 3 Tasks — Excel + PDF + Hardening

| Task | Description | Status |
|---|---|---|
| T20 | ExcelRenderer — ClosedXML, multi-sheet, freeze pane, auto-filter, SUM row | ✅ DONE |
| T21 | PdfRenderer — FastReport Open Source, A4 landscape, header/footer, SubReports | ✅ DONE |
| T21a | ~~QuestPDF license gate~~ — ELIMINATED. FastReport MIT adopted (ADR-005 superseded) | ✅ N/A |
| T22 | Wire all 3 formats via POST /api/v1/reports/{id}/execute | ✅ DONE |
| T23 | POST /api/v1/reports/{id}/preview — always JSON | ✅ DONE (wired in Sprint 2) |
| T24 | cost.cost-variance-analysis — CostVarianceFetcher built; register in Program.cs | ⏳ PENDING |
| T25 | Serilog CorrelationId propagation through pipeline | ⏳ PENDING |
| T26 | Coverage gate — ≥80% across Core + Infrastructure | ⏳ PENDING |
| T27 | IIS deployment to SRXWEBAPP1 (HTTPS + secrets + pre-deployment checklist) | ⏳ PENDING |
| T27a | Architecture Team formal approval for JSONL audit exception | ⏳ PENDING |
| T28 | Update decision-log.md + 06-known-risks-and-pitfalls.md | ⏳ PENDING |
| T29 | SETUP.md, DEPLOYMENT.md, docs/runbooks/TROUBLESHOOTING.md | ⏳ PENDING |
