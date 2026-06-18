# RSAI-NL — Natural Language Query Layer

**Version:** 0.1 (Sketch)
**Date:** April 2026
**Status:** Future iteration — not yet scheduled. Captured for planning purposes.
**Author:** Architecture discussion — hsalazar + Claude Code (April 2026)

---

## What This Iteration Delivers

A natural language query interface layered on top of the existing Reporting Service. Finance and
Operations users type questions in plain English via SM-Portal; the system translates them to
parameterised DB2 SQL using the Claude API, executes them through the existing `Db2DirectFetcher`
pipeline, and returns results with full transparency (SQL shown to user before or after execution).

This is **not a replacement** for the fixed-format Crystal Reports replacement programme (RSAI-1
through RSAI-6). Those reports remain as reproducible, auditable artefacts. This layer handles
ad-hoc queries that Crystal Reports never served at all.

---

## Problem Statement

Crystal Reports has zero ad-hoc capability. Finance asks: *"Which items have WAC more than 20%
above standard cost right now?"* — that requires a developer to write a new report, test it, and
deploy it. With RSAI-NL, Finance types the question and gets an answer in seconds.

This is the highest-value new capability that the Reporting Service can offer beyond Crystal
Reports replacement.

---

## Architecture

```
User (SM-Portal)
  │
  │  POST /api/v1/nl/query  { text, sessionId, format }
  ▼
NlQueryController  (new — Reporting.Api)
  │
  ▼
NlQueryService  (new — Reporting.Infrastructure)
  │
  ├─► ClaudeNlTranslator          Claude API (claude-sonnet-4-6)
  │     System prompt: MOVEX schema context + safety rules
  │     Returns: { sql, parameters, reportTitle, columns, confidence }
  │
  ├─► SqlSafetyValidator          Read-only enforcer (no DDL/DML, schema allowlist, CONO injection)
  │
  └─► ReportPipelineService       Existing pipeline — reused unchanged
        │
        ├─► Db2DirectFetcher      Existing — reused unchanged
        └─► IRenderer             Existing — JSON / Excel / PDF
```

The Claude API sits between user intent and the existing pipeline. It generates SQL — it never
executes SQL directly. All safety, schema enforcement, and execution remain in the existing .NET
service.

---

## New Components

### 1. `ClaudeNlTranslator` (Reporting.Infrastructure/Nl/)

Calls the Claude API (`claude-sonnet-4-6`) with:
- A system prompt containing the MOVEX schema context (see below)
- The user's natural language query
- Session history (last N turns, if multi-turn is enabled)

Returns a structured `NlTranslationResult`:

```csharp
public sealed record NlTranslationResult
{
    public string Sql { get; init; }                     // Generated SQL (positional ? params)
    public IReadOnlyList<object?> Parameters { get; init; } // Ordered param values
    public string ReportTitle { get; init; }             // Human-readable title for the result
    public IReadOnlyList<string> Columns { get; init; }  // Expected columns (for UI hints)
    public string Confidence { get; init; }              // "high" | "medium" | "low"
    public string? Clarification { get; init; }          // Non-null if Claude needs more info
    public string? ExplainedAs { get; init; }            // Plain-English restatement of what was understood
}
```

**Prompt structure (system):**

```
You are a SQL generator for a manufacturing ERP system. You translate natural language questions
into IBM i DB2 SQL queries. You MUST follow these rules exactly:

RULES:
- Generate SELECT statements only. Never INSERT, UPDATE, DELETE, DROP, CREATE, or EXEC.
- Always include CONO = ? as the first WHERE condition on every table (multi-company enforcement).
- Date fields in MOVEX are stored as INTEGER in YYYYMMDD format (e.g., 20260101 = 2026-01-01).
  Convert user-supplied dates using: INTEGER(TO_CHAR(date_value, 'YYYYMMDD')).
- String fields are CHAR (fixed-width, right-padded). Always TRIM() string columns in SELECT.
- Use positional parameters only (? not @param). List parameter values in order in your response.
- Prefix all table names with the schema: {schema} (e.g., mvxcdta.MITFAC).
- Never query tables outside this list: [MITFAC, FCAAVP, MITMAS, MITBAL, MITLOC, MITTRA,
  OOHEAD, OOLINE, ODHEAD, ODLINE, OCUSMA, MPHEAD, MPLINE, FGRECL, MACOPU, CIDMAS,
  FPLEDG, FSLEDG, FGLEDG, MWOHED, MMOMAT, MWOOPE, BPMOPO, CCURRA].
- If you cannot generate a safe, correct query, return clarification=true and explain why.
- Always add: FETCH FIRST {maxRows} ROWS ONLY (default 10000).

SCHEMA CONTEXT:
[injected from ai/context/nl-schema-context.md at runtime]

RESPONSE FORMAT (JSON only — no prose):
{
  "sql": "SELECT ...",
  "parameters": [value1, value2, ...],
  "reportTitle": "...",
  "columns": ["col1", "col2"],
  "confidence": "high|medium|low",
  "clarification": null | "string explaining what's unclear",
  "explainedAs": "Plain-English restatement of what was understood"
}
```

---

### 2. `SqlSafetyValidator` (Reporting.Infrastructure/Nl/)

Parses the generated SQL before execution. Rejects any query that:

- Contains keywords: `INSERT`, `UPDATE`, `DELETE`, `DROP`, `CREATE`, `ALTER`, `EXEC`,
  `EXECUTE`, `TRUNCATE`, `GRANT`, `REVOKE`
- Contains multiple statements (`;` followed by non-whitespace)
- References a table not in the approved allowlist
- Is missing `CONO = ?` in the WHERE clause
- Would return more than `MaxRows` (enforced via `FETCH FIRST N ROWS ONLY` check)

Logs every validation failure with CorrelationId for audit.

---

### 3. `NlQueryController` (Reporting.Api/Controllers/)

```
POST /api/v1/nl/query
{
  "text": "Show items where WAC is more than 20% above standard cost for facility 001",
  "sessionId": "optional — for multi-turn context",
  "format": "json | excel | pdf",
  "companyCode": "CMP100"
}

Response 200:
{
  "reportTitle": "Items with WAC > 20% Above Standard Cost — Facility 001",
  "explainedAs": "Items in facility 001 where unit cost (WAC) exceeds standard cost by more than 20%",
  "sql": "SELECT TRIM(f.CFITNO), ...",     ← always returned for transparency
  "executedAtUtc": "2026-04-15T03:22:00Z",
  "rowCount": 47,
  "data": { ... }    ← ReportDataSet JSON, or binary for excel/pdf
}

Response 400: clarification required
{
  "code": "CLARIFICATION_REQUIRED",
  "message": "Which facility are you asking about? Available: 001, 002, 003",
  "sessionId": "..."
}

Response 422: safety validation failed
{
  "code": "QUERY_REJECTED",
  "message": "Generated query failed safety validation.",
  "correlationId": "..."
}
```

---

### 4. `nl-schema-context.md` (ai/context/ — new file)

A markdown file injected into the Claude system prompt at startup. Contains:
- All 6 domain tables with column names, types, and plain-English descriptions
- MOVEX-specific conventions (YYYYMMDD dates, CHAR padding, CONO, schema prefix)
- Common join paths (e.g., MITFAC → MITMAS via CFITNO = MMITNO)
- Facility codes and company codes for Scanfil APAC
- Example queries (few-shot examples improve accuracy significantly)

This file is maintained in the repo alongside the code. When MOVEX schema changes, this file is
updated — the NL layer picks up the change on next restart.

---

## What Reuses Unchanged

| Component | Reuse |
|---|---|
| `Db2DirectFetcher` | 100% — NL queries run through the same fetcher |
| `ReportPipelineService` | 100% — NL result is a `ReportDataSet` like any other |
| `IRenderer` (JSON/Excel/PDF) | 100% — same output formats |
| `ApiKeyMiddleware` | 100% — same auth |
| `JsonReportCatalogProvider` | Unchanged — NL queries bypass the catalog |
| `ParameterValidator` | Not used for NL queries (parameters are LLM-generated) |
| Rate limiting | 100% — same fixed window applies |
| Serilog + CorrelationId | 100% — all NL pipeline stages log with CorrelationId |

---

## New NuGet Dependencies

| Package | Purpose | License |
|---|---|---|
| `Anthropic.SDK` or `Microsoft.Extensions.AI` | Claude API client | MIT |

No other new dependencies. The SQL parsing for safety validation uses `System.Text.RegularExpressions`
and string analysis — no SQL parser library needed at this scope.

---

## Architecture Decision: Show SQL to User

**Decision:** Always return the generated SQL in the API response. SM-Portal displays it
(collapsible, "View SQL" link) alongside the result.

**Rationale:** Finance users will trust output implicitly. The first wrong number — caused by a
subtly misunderstood query — will cause a crisis if there's no transparency. Showing the SQL:
- Lets a power user verify intent before acting on numbers
- Lets IT diagnose issues without access to logs
- Builds trust incrementally as users see that the system understands them correctly

This is non-negotiable for production use.

---

## Architecture Decision: Confirmation Mode (Optional)

The SM-Portal UI may offer a "confirm before execute" toggle. When enabled:
1. User submits NL query
2. SM-Portal calls `POST /api/v1/nl/translate` (new endpoint — returns SQL + explainedAs only, no execution)
3. User sees: *"I'll run: [plain-English restatement]. SQL: [collapsible]. Proceed?"*
4. User confirms → SM-Portal calls `POST /api/v1/nl/execute` with the pre-translated SQL

This eliminates surprise and builds user confidence without adding latency to the default flow.

---

## Risks & Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| SQL hallucination — plausible but wrong query | High | Show SQL to user; confidence score; few-shot examples in system prompt |
| IBM i DB2 dialect errors | Medium | System prompt explicitly documents positional params, YYYYMMDD ints, CHAR trim; integration test suite against real DB2 |
| User acts on wrong numbers | High | Transparency (SQL shown), ExplainedAs restatement, optional confirmation mode |
| Claude API unavailable | Low | NL endpoint returns 503 — fixed-format reports unaffected |
| Prompt injection via user query | Medium | System prompt uses Claude's instruction hierarchy; user input is never concatenated into SQL directly — only Claude's structured JSON response is used |
| Cost overrun on Claude API | Low | At 100-500 queries/day, claude-sonnet-4-6 cost is negligible (<$5/month at current pricing) |
| Schema drift (MOVEX upgrade) | Low | `nl-schema-context.md` is in the repo — update it as part of MOVEX upgrade process |

---

## Governance Requirements

| Gate | Detail |
|---|---|
| Architecture Team approval | Claude API introduces an external HTTP dependency from SRXWEBAPP1. IT must confirm outbound HTTPS to `api.anthropic.com:443` is permitted. |
| IT / Security review | LLM-generated SQL touching production DB2 is a new pattern. `SqlSafetyValidator` design must be reviewed before production deployment. |
| ADR required | New ADR (ADR-010) to document: Claude API choice, safety validator design, SQL transparency requirement, session handling decision. |

---

## Iteration Scope

### In Scope (RSAI-NL v1)

- Single-turn NL queries over the 6 approved MOVEX domain tables
- JSON, Excel, and PDF output (same as fixed-format reports)
- SQL transparency in all responses
- `SqlSafetyValidator` with table allowlist and DDL/DML rejection
- SM-Portal: NL query input box on the Report Browser page, result rendered as table + export buttons
- Cost Management and Inventory domain tables prioritised (highest Finance usage)

### Out of Scope (future)

- Multi-turn conversation / session memory (RSAI-NL v2)
- Scheduled NL reports ("run this query every Monday and email Finance")
- NL report *definition* generation (generating a new fixed-format report from description)
- Fine-tuning or custom model training on MOVEX data
- MovexDatawarehouse (SQL Server DW) — Phase 1 targets DB2 only; DW extension is straightforward

---

## Suggested Spike (Before Committing to Iteration)

Before scheduling RSAI-NL, run a ½-day spike:

1. Feed the Claude API a system prompt with 10 MOVEX table definitions + 5 few-shot examples
2. Submit 10 representative Finance questions
3. Measure: SQL correctness, dialect compliance (positional params, YYYYMMDD), hallucination rate
4. Execute the generated SQL against DB2 (dev CONO=300)
5. Decision gate: if ≥80% of queries produce correct results without manual correction → proceed

This spike costs ~2 hours and eliminates the primary unknown (LLM accuracy on IBM i DB2 dialect).

---

## Estimated Effort

| Component | Estimate |
|---|---|
| `ClaudeNlTranslator` + Claude API integration | 3 days |
| `SqlSafetyValidator` + tests | 2 days |
| `NlQueryController` + `/translate` + `/execute` endpoints | 2 days |
| `nl-schema-context.md` — schema grounding for all 6 domains | 3 days |
| SM-Portal NL query UI (input box, result display, SQL viewer) | 3 days |
| Integration tests against DB2 (CONO=300) | 2 days |
| ADR-010 + governance approvals | 1 day |
| **Total** | **~16 days (1 developer)** |

---

## Change Log

| Date | Change | Author |
|---|---|---|
| 2026-04-15 | Initial sketch created from architecture discussion | hsalazar + Claude Code |
