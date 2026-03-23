# Pre-Deployment Checklist — Reporting Service

Complete the appropriate section before any server deployment.
UAT is a subset of Production — all UAT gates also apply to Production.

| Environment | Runbook |
|---|---|
| UAT | [docs/runbooks/uat-deployment.md](../../docs/runbooks/uat-deployment.md) |
| Production | [docs/DEPLOYMENT.md](../../docs/DEPLOYMENT.md) |

---

## Section 1: UAT Deployment Checklist

> ⚠️ UAT and Production share the same SRXWEBAPP1 server and the same `/reporting` IIS application.
> UAT governance gates are intentionally lighter than Production.
> **Do not skip any item marked Required.**

### 1.1 Build and Test Gates

```
[ ] dotnet build Reporting.Service.sln -c Release — zero errors, zero warnings
[ ] dotnet test --collect:"XPlat Code Coverage" — all tests green
[ ] Coverage ≥ 80% (check tests/Reporting.Tests/TestResults/)
```

### 1.2 Route Audit

```
[ ] Grep controllers for [Route("reporting/ — must return zero matches
    Command: Select-String -Path "src\Reporting.Api\Controllers\*.cs" -Pattern '\[Route\("reporting/'
    Reason: IIS strips the /reporting prefix before forwarding to ASP.NET Core.
            Controllers must NOT include it. (SM-Portal Issue #7 — 8h lost)
```

### 1.3 Configuration Gates

```
[ ] web.config (deployed copy) — ASPNETCORE_ENVIRONMENT = UAT
    (source-controlled copy has Production — must be manually changed on server)
[ ] web.config — ASPNETCORE_CONTENTROOT = %APPL_PHYSICAL_PATH% present
    (without this, ContentRoot resolves to C:\Windows\System32 — SM-Portal Issue #1)
[ ] web.config — ASPNETCORE_URLS is NOT set
    (setting this causes Kestrel port conflict under InProcess hosting — SM-Portal Issue #4)
[ ] web.config — stdoutLogEnabled = false
    (enable only temporarily for startup debugging, never leave enabled)
[ ] web.config — REPORTING_SECRETS_PATH = C:\ProgramData\SRX\Reporting\secrets.json
[ ] appsettings.UAT.json present in publish output
    (activates: Swagger=true, rate=120/min, debug logging, SkipWeekends=false)
[ ] config/report-catalog.json present in publish output
```

### 1.4 Infrastructure Gates

```
[ ] App pool ReportingService: No Managed Code (managedRuntimeVersion=""), Integrated Pipeline
    Command: appcmd list apppool "ReportingService" /text:*
[ ] App pool idle timeout: 00:00:00 (disabled)
    Required for 23:00 UTC exchange rate sync. Default 20-min timeout kills the hosted service.
[ ] Only one app pool assigned to /reporting
    Command: appcmd list app "Default Web Site/reporting" → must show applicationPool:ReportingService
    (DefaultAppPool assigned = 500.35 errors — SM-Portal Issue #3)
[ ] Windows Authentication DISABLED on /reporting in IIS
    (Reporting-Service uses API Key auth only — Windows Auth causes middleware conflicts)
[ ] IIS URL Rewrite Module 2.1 installed
    Command: Get-WebGlobalModule -Name "RewriteModule"
    Required for HTTP→HTTPS redirect in web.config. (SM-Portal Issue #7)
[ ] secrets.json present: Test-Path C:\ProgramData\SRX\Reporting\secrets.json → True
[ ] secrets.json ACLs correct: Get-Acl ... shows IIS AppPool\ReportingService=Read ONLY
    (no other non-admin accounts in ACL)
[ ] secrets.json does not contain placeholder values
    (strings matching "your-", "placeholder", "changeme", "TODO")
[ ] IBM i ODBC DSN MOVEX_PROD configured for ReportingService app pool identity
[ ] HTTPS binding on Default Web Site (WR-5)
```

### 1.5 Post-Deploy Smoke Tests

```
[ ] GET https://srxwebapp1/reporting/api/v1/health → {"status":"Healthy"}
    If 502: secrets.json missing/unreadable — fix ACLs, NOT API key
    If 404: CONTENTROOT wrong or app not starting — check Windows Event Log (Source: IIS ANCM)
[ ] GET /api/v1/health/data-sources (with X-API-Key) → both IBM i DB2 and SQL Server DW = Healthy
    If 401: wrong API key (check X-API-Key header value)
    If 503: data source down — check ODBC DSN and SQL Server 150.3.20.116
[ ] Swagger UI accessible at https://srxwebapp1/reporting/swagger
    (CONFIRMS ASPNETCORE_ENVIRONMENT=UAT is active — if Swagger is missing, web.config still has Production)
[ ] GET /api/v1/reports?domain=CostManagement → 3 report definitions in JSON array
[ ] Rate limit fires at ~120 req/min — NOT ~60
    (If 429 fires at ~61: Production appsettings active — ASPNETCORE_ENVIRONMENT is wrong)
[ ] SM-Portal team notified — Reporting Service is healthy, proceed with SM-Portal deployment
```

### 1.6 Agent Sign-Off (UAT)

| Agent | Responsibility | Sign-Off |
|---|---|---|
| **developer-dotnet** | Build clean, config correct, `appsettings.UAT.json` present, `web.config` ASPNETCORE_ENVIRONMENT=UAT | [ ] |
| **validator-iis-deploy** | App pool settings, one pool only, CONTENTROOT present, URL Rewrite installed, smoke tests 1–5 passed | [ ] |
| **developer-integration** | `/api/v1/health/data-sources` — both sources Healthy | [ ] |

---

## Section 2: Production Deployment Checklist

> **All UAT gates (Section 1) must be completed first.**
> Production adds governance approvals and stricter security gates.
> The environment variable in web.config must be `Production`, not `UAT`.

### 2.1 WORKSPACE_RULES.md Compliance

```
[ ] WR-2: Azure Key Vault is NOT yet active. ADR-009 (NTFS secrets file) is the approved interim
          approach. Verify secrets.json is in place per Section 1.4.
          If Azure Key Vault has been provisioned (ADR-006 resolved), update this item.
[ ] WR-3: README.md present with all mandatory sections
[ ] WR-4: web.config (deployed copy) has ASPNETCORE_ENVIRONMENT = Production
          Verify Swagger is NOT accessible: browsing /swagger must return 404 or 401
[ ] WR-5: HTTPS binding confirmed (from Section 1.4)
[ ] WR-6: All runbooks in docs/runbooks/ — not docs/ directly
```

### 2.2 Pending Approvals (Production Only)

These gates are NOT required for UAT. All must be obtained and documented in
`ai/memory/08-governance-and-decisions.md` before the first Production deployment.

```
[ ] WR-1: Architecture Team formal approval for JSONL audit logging exception
          Approver: ___________________ Date: ___________ Decision: ___________
          Document location: ai/memory/08-governance-and-decisions.md

[ ] T21a: QuestPDF Community Edition — IT/Legal review of license terms
          Approver: ___________________ Date: ___________ Decision: ___________
          Fallback: PdfSharp (MIT) if disqualified
          Document location: ai/memory/08-governance-and-decisions.md

[ ] ADR-008: Architecture Team approval for DB2 CCURRA INSERT (exchange rate sync)
             Approver: ___________________ Date: ___________ Decision: ___________
             Document location: ai/memory/08-governance-and-decisions.md

[ ] ADR-006: IT confirmation of Azure Key Vault availability on SRXWEBAPP1
             Status: ___________________ (or Infrastructure Exception filed with IT)
             Document location: ai/memory/08-governance-and-decisions.md
```

### 2.3 Security Gates (Production Only, in addition to Section 1.3–1.4)

```
[ ] API keys rotated from UAT values (if UAT and Production use different keys)
    Document your key rotation policy: ___________________________
[ ] No development or UAT secrets in production secrets.json
[ ] Rate limit = 60 req/min (NOT 120 — if 429 fires at ~121, UAT appsettings are active)
[ ] API key timing-safe comparison in place (CryptographicOperations.FixedTimeEquals)
    This is already in ApiKeyMiddleware — verify it has not been altered
[ ] Health endpoint accessible WITHOUT API key: GET /api/v1/health → 200
[ ] Swagger UI is NOT accessible on Production server
    Verify: browsing https://srxwebapp1/reporting/swagger returns 404 or 401
    (If Swagger loads: ASPNETCORE_ENVIRONMENT=UAT is still set in web.config — critical)
```

### 2.4 Functional Gates

```
[ ] GET /api/v1/health → {"status":"Healthy"}
[ ] GET /api/v1/health/data-sources → both IBM i DB2 and SQL Server DW = Healthy
[ ] GET /api/v1/reports?domain=CostManagement → 3 report definitions
[ ] POST /api/v1/reports/cost.average-cost-snapshot/execute → JSON with real data
[ ] Excel download verified: freeze pane, auto-filter, SUM row
[ ] PDF download verified: SRX letterhead, A4 landscape, page numbers
[ ] Rate limit fires at ~60 req/min (send 65 requests, confirm 429 starts at ~61)
[ ] GET /api/v1/exchange-rates/USD/<today> → valid rate response
```

### 2.5 Deploy Order

```
1. Deploy Reporting Service FIRST
2. Verify: GET /api/v1/health → {"status":"Healthy"}
3. Deploy SM-Portal
4. Verify: /reports route in SM-Portal loads report data
```

### 2.6 Agent Sign-Off (Production)

| Agent | Responsibility | Sign-Off |
|---|---|---|
| **developer-dotnet** | Build clean, WR-3/4/5/6 compliance, `web.config` ASPNETCORE_ENVIRONMENT=Production | [ ] |
| **validator-iis-deploy** | IIS audit, one pool only, URL Rewrite installed, Swagger inaccessible, rate limit = 60 | [ ] |
| **developer-integration** | DB2, SQL Server DW, RBA outbound HTTPS all verified | [ ] |
| **architect-system-design** | WR-1, T21a, ADR-008, ADR-006 approvals documented in 08-governance-and-decisions.md | [ ] |
