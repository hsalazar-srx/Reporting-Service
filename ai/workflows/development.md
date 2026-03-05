# Development Workflow — Reporting Service

---

## Standard Session Workflow

**Start of session:**
1. Read `ai/rules.md` → `ai/memory/00-product-vision.md` → `ai/tasks/sprint-backlog.md`
2. Check `ai/memory/06-known-risks-and-pitfalls.md` for relevant warnings
3. Mark the current task as `in_progress` in `ai/tasks/sprint-backlog.md`

**During session:**
- Check skills registry before implementing anything new
- Reference skills in code comments: `// Uses skill: data/report-generation v1.0`
- Use `/compact` at 50% context usage
- Commit each completed task (not end-of-day batching)

**End of session:**
- Run pre-commit checklist (`ai/checklists/pre-commit.md`)
- Update `ai/evidence/decision-log.md` if any architectural decisions made
- Update `ai/tasks/sprint-backlog.md` task status
- Commit with Conventional Commits format

---

## Decision Framework

```
Is it mentioned in ai/memory/ files?
  ├─ YES → Follow documented decision (don't re-litigate)
  └─ NO → Is it a small decision (low risk)?
           ├─ YES → Document in decision-log.md and proceed
           └─ NO → Is it an ADR matter?
                    ├─ YES → Write ADR, get Architecture Team approval
                    └─ NO → Escalate to Dev Lead
```

---

## Escalation

### Escalate to Dev Lead
- DB2 ODBC driver not available on target server
- IBM i schema name incorrect for environment
- SQL Server DW connection failing (150.3.20.116)
- Ambiguous domain boundary (which iteration owns a MOVEX table)

### Escalate to Architecture Team
- Audit logging strategy change needed
- RBAC enforcement urgency escalated
- New domain boundary disputes
- Azure Key Vault unavailable (Infrastructure Exception required)

### Escalate to IT/Legal
- QuestPDF license decision (before T27)
- Azure Key Vault provisioning

### Stop Immediately
- 🔴 API key exposed in logs or source control
- 🔴 Service is writing data (should be read-only only)
- 🔴 Production deployment failed uncontrolled

---

## Git Workflow

```
main branch         → production-ready only (no direct pushes)
feature/rsai-1-t1   → Sprint 1 scaffolding
feature/rsai-1-t3   → JsonReportCatalogProvider
feature/rsai-1-t4   → ApiKeyMiddleware
... (one branch per task)
```

PR required before merging to main. Small PRs. Conventional Commits.

---

## Context Window Efficiency

Each Claude session should target ONE task from sprint-backlog.md:

1. Read `ai/rules.md` + `ai/memory/00-product-vision.md` + current task (3-4 file reads to restore context)
2. Work on one task
3. Update `ai/tasks/sprint-backlog.md` + `ai/tasks/todo.md`
4. Commit
5. End session or start next task in new compact context

Use `/compact` when context reaches ~50%. Do not try to complete an entire sprint in one session.
