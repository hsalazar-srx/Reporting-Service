# Living Task List — Reporting Service

_Updated as work progresses. This file is the resume point for new sessions._

## Current Focus: Sprint 3 — Hardening

**Sprint 1 complete.** Build: ✅ clean. Tests: ✅
**Sprint 2 complete.** Pipeline wired end-to-end. JSON execution live.
**Sprint 3 rendering complete.** Excel (ClosedXML) + PDF (FastReport MIT) built and tested.
**Tests: ✅ 120/120 passing.**

### Next Up
- [ ] T24: Register CostVarianceFetcher in Program.cs (fetcher exists — just needs DI wiring)
- [ ] T25: Serilog CorrelationId propagation through pipeline stages
- [ ] T26: Coverage gate — run dotnet-coverage, verify ≥80% on Core + Infrastructure

### Up Next After That
- [ ] T27: IIS deployment to SRXWEBAPP1 (pre-deployment checklist)
- [ ] T27a: Architecture Team formal approval for JSONL audit exception
- [ ] T28: Update decision-log.md + 06-known-risks-and-pitfalls.md
- [ ] T29: SETUP.md, DEPLOYMENT.md, docs/runbooks/TROUBLESHOOTING.md

### Blocked / Needs Input
- T0: Crystal Reports inventory — awaiting stakeholder list
- T27a: Architecture Team audit exception approval
- ADR-008: Architecture Team DB2 write approval for exchange rate sync

---

_See ai/tasks/sprint-backlog.md for full task list with descriptions._
