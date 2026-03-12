# BI Platform Proposal — Self-Hosted Power BI Replacement

**Version:** 1.0
**Date:** March 6, 2026
**Status:** PROPOSAL — Pending Architecture Team review
**Origin:** Strategic analysis session — BI platform evaluation for post-Crystal-Reports roadmap

---

## Problem Statement

Crystal Reports is being replaced by the Reporting Service (.NET 8 API), which handles
**operational, paginated, print-ready** reports (PDF/Excel). However, Crystal Reports'
replacement does not provide:

- Interactive dashboards and KPI monitoring (Power BI-like)
- Self-service data exploration for analysts (ad-hoc SQL/filtering)
- Slice-and-dice analytics across manufacturing domains
- Embedded charts/visualisations within SM-Portal beyond Recharts static views
- A semantic layer where business metrics are defined centrally once, reused everywhere

This gap currently means analysts continue using manual Excel processes (e.g., the 87-step
DIFOT process) even after the Reporting Service is deployed.

---

## Evaluated Candidates

| Platform | License | Self-hosted Windows | Crystal Replacement | Analytics / BI |
|---|---|---|---|---|
| Apache Superset | Apache 2.0 | Docker (Linux container) | Partial | ✅ Best-in-class OSS |
| Metabase OSS | AGPL-3.0 | ✅ JAR on Windows | Weak | ✅ Good, simple UX |
| JasperReports CE | AGPL-3.0 | ⚠️ Java runtime | ✅ Strong | Weak |
| Grafana OSS | AGPL-3.0 | ✅ Windows installer | ❌ Poor | ⚠️ Time-series only |
| SSRS | SQL Server license | ✅ Native IIS/Windows | ✅ Strong | ❌ No analytics |

### Key insight from evaluation

No single OSS tool covers both workloads equally:
- __Crystal replacement__ (paginated, banded, parameterised, print-fidelity) → The Reporting
  Service API already solves this with QuestPDF + ClosedXML
- __Power BI replacement__ (interactive dashboards, semantic layer, self-service) → Apache Superset

The two tools are **complementary, not competing**.

---

## Recommended Architecture

### Dual-layer approach

```
DATA LAYER
  IBM i DB2 (MOVEX AS/400)
       │ ODBC (existing)
       ▼
  SQL Server DataWarehouse (150.3.20.116)
       │ (shared foundation)
       ├─────────────────────────────────────┐
       ▼                                     ▼
  Reporting Service (.NET 8 API)         Apache Superset (Docker)
  ─────────────────────────────          ─────────────────────────
  • Paginated PDF reports (QuestPDF)     • Interactive dashboards
  • Excel exports (ClosedXML)            • Self-service SQL Lab
  • Report catalog / pipeline            • Semantic layer (metrics)
  • Audit logging, compliance            • Row-level security
  • API-driven — SM-Portal proxy         • Scheduled alerts/emails
  • Crystal Reports replacement          • Power BI replacement
```

SM-Portal acts as the **unified entry point** for users:
- Report downloads and previews → proxy to Reporting Service API
- Embedded interactive dashboards → guest tokens from Superset

---

## Platform: Apache Superset

### Why Superset over alternatives

| Criterion | Rationale |
|---|---|
| **Governance** | Apache Software Foundation — no risk of license change (vs. Elastic, HashiCorp pattern) |
| **Semantic layer** | Native virtual datasets + metrics: define "cost per unit" once, reuse everywhere |
| **SQL connectivity** | SQLAlchemy-based — connects to SQL Server, DB2, Postgres, Snowflake without migration |
| **Embedding** | Guest token API enables SM-Portal to embed dashboards without separate login |
| **REST API** | Full CRUD API — Reporting Service can orchestrate Superset programmatically |
| **Self-service** | SQL Lab provides Power BI-like ad-hoc exploration, accessible without developer support |
| **Row-level security** | Native domain-level data isolation per user/role without custom code |
| **Community** | 60k+ GitHub stars, Apache stewardship, monthly releases — not declining like Jasper |
| **Export** | PDF/Excel from dashboards (headless Chrome renderer + Celery worker) |

### Superset covers the key OSS concerns flagged in earlier iterations

- **SSRS** would replace Crystal with another Microsoft legacy tool — chosen as maintenance mode,
  no self-service analytics capability, no semantic layer, dead end for modernisation.
- **JasperReports** community is declining post-TIBCO/Cloud Software Group acquisition;
  Java runtime adds operational complexity with no .NET benefit.
- **Metabase** simpler to start but outgrown faster: weaker semantic layer, AGPL licence
  implications, limited row-level security in OSS edition.

---

## Deployment Model

Docker Compose on a dedicated host (or separate from SRXWEBAPP1 to avoid resource contention):

**Components:**
- `apache/superset` — web/API process (port 8088, reverse-proxied via IIS or nginx)
- `postgres:16-alpine` — Superset metadata database (not reporting data)
- `redis:7-alpine` — Query result cache + async task queue
- `superset-worker` — Celery worker for scheduled reports, PDF thumbnail rendering
- `superset-beat` — Celery scheduler for alert/report delivery

**Key configuration:**
- `FEATURE_FLAGS.EMBEDDED_SUPERSET = True` — SM-Portal integration
- `FEATURE_FLAGS.ALERT_REPORTS = True` — scheduled email delivery
- `FEATURE_FLAGS.ENABLE_TEMPLATE_PROCESSING = True` — Jinja2 in SQL (dynamic parameters)
- `ROW_LEVEL_SECURITY = True` — domain isolation
- `SESSION_COOKIE_SECURE = True`, `TALISMAN_ENABLED = True` — TLS compliance

**Auth path:**
- Short-term: Superset local auth + API Key for service-to-service
- Target: Azure AD OAuth2/OIDC (same planned direction as the Reporting Service)

---

## Data Connectivity

| Source | Method | Notes |
|---|---|---|
| SQL Server DataWarehouse | `mssql+pyodbc` via SQLAlchemy | Primary source for all dashboards |
| IBM i DB2 (direct) | `ibm_db_sa` Python package | For near-real-time datasets not yet in DW |

SQL Server DataWarehouse should remain the **primary semantic layer** for Superset.
DB2 direct access is secondary — used only when DW coverage is insufficient.

---

## SM-Portal Integration (Embedded Dashboards)

Embedding flow using guest tokens:

```
SM-Portal (.NET)                            Superset
  1. POST /api/v1/security/guest_token/  ──►
     { "user": {...}, "resources": [...] }
  2.                                     ◄── { "token": "eyJ..." }
  3. Render <iframe src="superset/embedded/{id}?...token">
     in SM-Portal view component         ──► Browser renders dashboard
```

Users see interactive dashboards embedded inside SM-Portal without a separate Superset login.
Reporting Service continues to serve PDF/Excel downloads from the same portal.

---

## Team Requirements

| Role | Responsibility | Status |
|---|---|---|
| BI / Data Engineer (Python) | Superset deployment, semantic layer, ETL views, dashboard authoring, user training | ✅ **Confirmed** |
| .NET Developer(s) | Guest token integration, SM-Portal embedding, Reporting Service continued development | ✅ Existing team |

The BI/Data Engineer position is confirmed. This person owns the full Superset layer end-to-end.

---

## Proposed Roadmap (Post-RSAI-6)

| Phase | Timeline | Deliverables |
|---|---|---|
| **RSAI-BI-0: Infrastructure** | Weeks 1–2 | Docker host provisioned. Superset deployed. SQL Server DW connected. Connectivity validated. |
| **RSAI-BI-1: Semantic Foundation** | Weeks 3–4 | Virtual datasets and metrics for Cost Management domain. 2–3 pilot dashboards. Internal demo. |
| **RSAI-BI-2: SM-Portal Embedding** | Weeks 5–6 | Guest token integration. First dashboard embedded in SM-Portal. Users experience unified portal. |
| **RSAI-BI-3: SQL Lab Rollout** | Weeks 7–8 | SQL Lab enabled for power users. Row-level security per domain. Analyst training. |
| **RSAI-BI-4: All 6 Domains** | Weeks 9–16 | Dashboards for all 6 manufacturing domains. Scheduled email delivery (Celery alerts). |
| **RSAI-BI-5: DB2 Direct** | Weeks 17–20 | IBM i DB2 connected for near-real-time datasets. Operational monitoring dashboards. |

---

## Impact on Existing Reporting Service

The Reporting Service is **not replaced** — it evolves to be the operational/compliance layer
while Superset owns the analytics layer:

| Concern | Reporting Service (stays) | Superset (adds) |
|---|---|---|
| Paginated PDF reports | ✅ Primary | ❌ |
| Excel exports | ✅ Primary | ❌ |
| Interactive dashboards | ❌ | ✅ Primary |
| Ad-hoc data exploration | ❌ | ✅ Primary |
| Audit trail / compliance | ✅ Primary | ❌ |
| SM-Portal proxy entry point | ✅ Primary (API) | ⚠️ Embedded (iframe) |
| Scheduling / email delivery | RSAI-7 (planned) | ✅ Built-in now |

**Potential future optimisation:** Reporting Service delegates paginated PDF rendering to Superset
via its export API, removing QuestPDF dependency and resolving the pending license review (T21a).
This is an evaluation item for RSAI-BI-3+.

---

## Risks and Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Docker host provisioning delay | Medium | Can run on SRXWEBAPP1 initially; separate host recommended long-term |
| DB2 JDBC/Python driver for IBM i | Medium | SQL Server DW is primary; DB2 direct is optional — risk isolated to RSAI-BI-5 |
| Guest token auth before Azure AD is ready | Low | Superset local auth works for initial rollout; Azure AD added when available |
| QuestPDF license forces pivot to Superset PDF | Low | Superset export API is a viable fallback; evaluate at RSAI-BI-3 |

---

## Open Questions for Architecture Team

1. Is there a preference for a dedicated Docker host vs. containerised on SRXWEBAPP1?
2. Should the Superset metadata PostgreSQL database be managed by IT infrastructure or the team?
3. Does row-level security need to integrate with AD groups from day one, or can local auth suffice for the pilot?
4. Should scheduled report delivery in Superset (Celery alerts) replace or complement the RSAI-7 Scheduler Service?

> ~~BI/Data Engineer headcount~~ — **Resolved.** Position confirmed.

---

## Status

| Item | Status |
|---|---|
| Architecture analysis completed | ✅ March 6, 2026 |
| ADR-009 draft created | ✅ March 6, 2026 |
| Architecture Team review | ⏳ Required before RSAI-BI-0 |
| Stakeholder approval | ⏳ Required before infrastructure provisioning |
| Formal BI roadmap entry | ⏳ Pending approval |

---

## References

- [ADR-009 in decision-log.md](../evidence/decision-log.md) — Architecture Decision Record (draft)
- [07-product-roadmap.md](./07-product-roadmap.md) — RSAI-BI phase entries
- [WORKSPACE_RULES.md](../../../../.github/WORKSPACE_RULES.md) — Security and compliance standards
- Apache Superset: https://superset.apache.org
- Superset Embedded SDK: https://superset.apache.org/docs/embedded-superset/
