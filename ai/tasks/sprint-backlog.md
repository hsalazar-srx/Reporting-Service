# Sprint Backlog — RSAI-1

**Sprint 1 — Foundation Skeleton**
**Goal:** Deployable service with catalog browsing and health checks. No reports executed yet.

---

## Sprint 1 Tasks

| Task | Description | Status |
|---|---|---|
| T0 | Collect Crystal Reports inventory from stakeholders | ⏳ PENDING (before Sprint 2) |
| T1 | Solution scaffold + full ai/ workspace | ✅ DONE |
| T2 | MAS updates: expert-reporting-analytics agent + 4 new skills | ⏳ PENDING |
| T3 | JsonReportCatalogProvider (with last-good-config fallback) | ⏳ PENDING |
| T4 | ApiKeyMiddleware (FixedTimeEquals + rate limiting) | ⏳ PENDING |
| T5 | ReportController GET /api/v1/reports (versioned) + ErrorResponse | ⏳ PENDING |
| T5b | Swashbuckle/OpenAPI (disabled in prod) | ⏳ PENDING |
| T6 | HealthController GET /api/v1/health + /data-sources | ⏳ PENDING |
| T7 | config/report-catalog.json — 3 Cost Management entries | ⏳ PENDING |
| T8 | appsettings.json — all config sections | ⏳ PENDING |
| T9 | Tests: CatalogProvider, ParameterValidator, ApiKeyMiddleware | ⏳ PENDING |

**Sprint 1 Done When:**
`GET /api/v1/reports?domain=CostManagement` returns 3 validated report definitions;
rate limit returns 429 after 60 req/min; invalid catalog retains last-good-config.

---

## Sprint 2 Tasks — DB2 Data Pipeline + JSON Output

| Task | Description | Status |
|---|---|---|
| T10 | Core interfaces: IDataFetcher, ITransformer, IRenderer, ReportDataSet, ReportOutput | ⏳ PENDING |
| T11 | ReportPipelineService — orchestrates all pipeline stages | ⏳ PENDING |
| T12 | Db2DirectFetcher base — ODBC positional params, Polly from scratch | ⏳ PENDING |
| T12a | ReportDataSet.ExecutedAtUtc — stamped at fetch start | ⏳ PENDING |
| T13 | MovexDateConverter — YYYYMMDD INT ↔ DateTime | ⏳ PENDING |
| T14 | AverageCostFetcher — MITFAC + MITMAS SQL (parameterised) | ⏳ PENDING |
| T15 | WacHistoryFetcher — FCAAVP CTE SQL (parameterised) | ⏳ PENDING |
| T16 | CostManagementTransformer — group by Facility → ItemType → Item | ⏳ PENDING |
| T17 | JsonRenderer — serialise ReportDataSet with SubReports support | ⏳ PENDING |
| T18 | POST /api/v1/reports/{id}/execute wired for JSON format | ⏳ PENDING |
| T19 | Tests: AverageCostFetcher, WacHistoryFetcher, Transformer, Pipeline | ⏳ PENDING |

---

## Sprint 3 Tasks — Excel + PDF + Hardening

| Task | Description | Status |
|---|---|---|
| T20 | ClosedXmlRenderer — multi-sheet, freeze pane, auto-filter, SUM row | ⏳ PENDING |
| T21 | QuestPdfRenderer — SRX letterhead, A4 landscape, page numbers | ⏳ PENDING |
| T21a | QuestPDF license review gate — IT/Legal sign-off before T27 | ⏳ PENDING |
| T22 | Wire all 3 formats via POST /api/v1/reports/{id}/execute | ⏳ PENDING |
| T23 | POST /api/v1/reports/{id}/preview — always JSON | ⏳ PENDING |
| T24 | cost.cost-variance-analysis report — standard vs WAC variance | ⏳ PENDING |
| T25 | Serilog CorrelationId propagation through pipeline | ⏳ PENDING |
| T26 | Coverage gate — ≥80% across Core + Infrastructure | ⏳ PENDING |
| T27 | IIS deployment to SRXWEBAPP1 (HTTPS + Azure KV + pre-deployment checklist) | ⏳ PENDING |
| T27a | Architecture Team formal approval for JSONL audit exception | ⏳ PENDING |
| T28 | Update decision-log.md + 06-known-risks-and-pitfalls.md | ⏳ PENDING |
| T29 | SETUP.md, DEPLOYMENT.md, docs/runbooks/TROUBLESHOOTING.md | ⏳ PENDING |
