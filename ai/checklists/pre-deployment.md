# Pre-Deployment Checklist

Run before deploying to SRXWEBAPP1 (production):

## WORKSPACE_RULES.md Compliance (10 items)

```
[ ] WR-1: Architecture Team formal approval for JSONL audit exception obtained
         Document in ai/memory/08-governance-and-decisions.md (approver + date)
[ ] WR-2: Azure Key Vault configured for all production secrets (not IIS env vars)
         Or Infrastructure Exception filed with IT and documented
[ ] WR-3: README.md at project root with all mandatory sections
[ ] WR-4: Swagger/OpenAPI disabled in production (Swagger:EnableUI = false)
[ ] WR-5: HTTPS binding configured in IIS / web.config (TLS 1.2+)
[ ] WR-6: Runbooks in docs/runbooks/ (not docs/)
[ ] Schema created with standard template (if audit logging active)
[ ] TDE enabled on SQL Server database (when audit active)
[ ] Retention policy documented (7 years minimum)
[ ] Backup schedule verified (daily minimum)
```

## QuestPDF License Gate (T21a)

```
[ ] IT/Legal has reviewed QuestPDF Community Edition license terms
    Document in ai/memory/08-governance-and-decisions.md (approver + date)
    OR: Switch to PdfSharp (MIT) if disqualified
```

## Security Gates

```
[ ] API keys rotated from development values
[ ] No development secrets in production configuration
[ ] Rate limiting verified (429 returned after 60 req/min)
[ ] API key timing-safe comparison in place (CryptographicOperations)
[ ] Health endpoint accessible without API key
[ ] Swagger UI disabled (only enable in Development environment)
```

## Functional Gates

```
[ ] dotnet build — zero warnings in Release configuration
[ ] dotnet test — all tests green
[ ] GET /api/v1/health → 200 OK on target server
[ ] GET /api/v1/health/data-sources → both IBM i DB2 and SQL Server DW respond
[ ] GET /api/v1/reports?domain=CostManagement → 3 report definitions
[ ] POST /api/v1/reports/cost.average-cost-snapshot/execute → JSON with real data
[ ] Excel download verified: freeze pane, auto-filter, SUM row
[ ] PDF download verified: SRX letterhead, A4 landscape, page numbers
```

## Deployment Order

```
1. Deploy Reporting Service to SRXWEBAPP1 FIRST
2. Verify health check passes
3. Deploy SM-Portal changes SECOND
4. Verify /reports route in SM-Portal
```
