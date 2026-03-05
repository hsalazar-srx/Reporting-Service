# Execution Plan — RSAI-1

**Approach:** Iterative MVAI delivery — 3 sprints, each deliverable at the end of every sprint.

## Sprint 1 — Foundation Skeleton (~1 week)
**Deliverable:** Deployable service with catalog browsing and health checks.

Sequence: T1 → T2 → T3 → T4 → T5 → T5b → T6 → T7 → T8 → T9

## Sprint 2 — DB2 Data Pipeline (~1 week)
**Deliverable:** Real IBM i data returned as JSON from Cost Management reports.

Sequence: T10 → T11 → T12 → T12a → T13 → T14 → T15 → T16 → T17 → T18 → T19

⚠️ Before T14: Obtain Crystal Reports inventory from stakeholders (T0)

## Sprint 3 — Excel + PDF + Hardening (~1 week)
**Deliverable:** All 3 output formats, production-ready.

Sequence: T20 → T21 → T21a (gate) → T22 → T23 → T24 → T25 → T26 → T27a (gate) → T27 → T28 → T29

## Critical Path Dependencies

- T7 (catalog) must exist before T3 (catalog provider) can be fully tested
- T21a (QuestPDF license) must be approved before T27 (production deploy)
- T27a (Architecture Team) must be approved before T27 (production deploy)
- T0 (Crystal Reports inventory) must be completed before T14 (AverageCostFetcher)
