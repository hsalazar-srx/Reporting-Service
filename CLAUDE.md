# Reporting Service: Claude Code Instructions

**Project:** .NET 8.0 ASP.NET Core REST API — Crystal Reports replacement
**Phase:** RSAI-1 (Cost Management — Sprint 1)
**Critical:** Read [ai/rules.md](ai/rules.md) FIRST every session

---

## Quick Start

**First session?** Read [ai/rules.md](ai/rules.md) → [00-product-vision.md](ai/memory/00-product-vision.md) → [00-skills-audit.md](ai/memory/00-skills-audit.md)

**Before coding:** Check skills registry → Update audit → Reference in code

**Current work:** [sprint-backlog.md](ai/tasks/sprint-backlog.md)

---

## Critical Rules (Never Violate)

### Workspace Standards (WORKSPACE_RULES.md)
- ✅ **SQLite** for audit logs (7-year retention) — deferred to Iteration 2 with Architecture Team approval
- ✅ **TLS 1.2+** for all connections (HTTPS binding in IIS)
- ✅ **Secrets** — **INTERIM (ADR-009):** NTFS-protected `C:\ProgramData\SRX\Reporting\secrets.json` outside deployment folder. **Never** add secrets to `web.config` or `appsettings*.json`. Upgrade to Azure Key Vault when IT provisions it (ADR-006).
- ✅ **≥80%** test coverage (Core + Infrastructure)
- ✅ **Never log PII** (customer names, identifiers)
- ✅ **TDE** at rest, TLS in transit

### Project-Specific Critical Constraints
- ❌ **Never** use named params (`@`) in DB2 ODBC queries — positional only (`?`)
- ❌ **Never** use `string.Equals` for API key comparison — use `CryptographicOperations.FixedTimeEquals`
- ❌ **Never** access MITBAL (Inventory) in Cost Management domain — Iteration 4 table
- ❌ **Never** assume Polly is in DirectQueryDataSource template — implement from scratch in Db2DirectFetcher
- ✅ **Always** include `ExecutedAtUtc` in ReportDataSet
- ✅ **Always** prefix DB2 tables: `{schema}.{table}` (configurable, e.g., `mvxcdta.MITFAC`)
- ✅ **Always** `.Trim()` string results from DB2 CHAR columns
- ✅ **QuestPDF license** IT/Legal review required before production (T21a gate)

---

## Context Management

**Prevent bloat:** Use /plan for new features • Scope to ONE task per session • /compact at 50% • Commit per task

**Resume:** Read `ai/tasks/todo.md` → `ai/tasks/sprint-backlog.md` to find current task

---

## Git Workflow

### Branch Protection
- ❌ **Never push directly to `main`** — all changes through feature branch + PR
- Branch naming: `feature/rsai-1-t{N}-description` (e.g., `feature/rsai-1-t3-catalog-provider`)
- PR required before merging to main

### Commit Format (Conventional Commits)
```
feat: add JsonReportCatalogProvider with last-good-config fallback (T3)
fix: replace string.Equals with CryptographicOperations in ApiKeyMiddleware (T4)
test: add ApiKeyMiddleware timing-safe comparison tests (T9)
```

---

## Skills-First Pattern

```csharp
// Before implementing ANY feature, check C:\Projects\.github\skills\manifest.json
// Document in ai/memory/00-skills-audit.md

/// <summary>
/// Fetches cost data from IBM i DB2.
/// Uses skill: integration/movex-db2-data-source v1.0
/// Uses skill: architecture/resilience-patterns v1.0
/// </summary>
public class Db2DirectFetcher : IDataFetcher { ... }
```

---

## Key Patterns

- **DB2 ODBC:** See [ai/patterns/db2-odbc.md](ai/patterns/db2-odbc.md) — positional params, schema, Polly
- **Pipeline:** See [ai/patterns/report-pipeline.md](ai/patterns/report-pipeline.md)
- **Errors:** See [ai/patterns/error-handling.md](ai/patterns/error-handling.md)
- **Config:** See [ai/patterns/configuration.md](ai/patterns/configuration.md)

---

## Quality Gates

**Before commit:** Build passes, tests pass (≥80%), no credentials/PII → [Full checklist](ai/checklists/pre-commit.md)

**Before deploy:** WORKSPACE_RULES compliance + QuestPDF license + Architecture Team approval → [Full checklist](ai/checklists/pre-deployment.md)

---

## Escalate

**Dev Lead:** Broken DB2 connection, wrong schema, ambiguous domain boundary
**Architecture Team:** Audit strategy change, Azure KV unavailable
**IT/Legal:** QuestPDF license, Azure KV provisioning
**Stop immediately:** API key in logs, service writing data (read-only only)

---

## Documentation

| Quick Access | Detailed Knowledge |
|---|---|
| [ai/rules.md](ai/rules.md) — Critical rules | [ai/memory/](ai/memory/) — Knowledge base |
| [ai/tasks/sprint-backlog.md](ai/tasks/sprint-backlog.md) — Current work | [ai/patterns/](ai/patterns/) — Code patterns |
| [ai/checklists/](ai/checklists/) — Quality gates | [ai/evidence/decision-log.md](ai/evidence/decision-log.md) — ADRs |
| [ai/tasks/todo.md](ai/tasks/todo.md) — Resume here | [ai/memory/06-known-risks-and-pitfalls.md](ai/memory/06-known-risks-and-pitfalls.md) — 15 gaps |

---

## Session Checklist

**Start:** Read sprint-backlog.md → mark task in_progress
**During:** Check skills → Write tests first → /compact at 50%
**End:** Tests pass → No credentials/PII → Commit → Push to feature branch

---

**Based on:** CLAUDE.md.template v2.1
**Last Updated:** March 2026
