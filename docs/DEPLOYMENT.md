# Deployment Guide — Reporting Service

This guide covers the two server-deployed environments: **UAT** and **Production**.
Both run on the same server (SRXWEBAPP1) at the same IIS application path (`/reporting`).
The environment is controlled by `ASPNETCORE_ENVIRONMENT` in the deployed `web.config`.

For local development, use `dotnet run` — see [docs/SETUP.md](SETUP.md).

---

## Environment Comparison

| Concern | Development | UAT | Production |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` (launchSettings.json) | `UAT` (web.config — set manually) | `Production` (web.config) |
| Swagger UI | Enabled at `localhost:5160/swagger` | Enabled at `/reporting/swagger` | **Disabled** (WR-4) |
| Log level | Debug | Debug | Information |
| Rate limit | 60 req/min | **120 req/min** (appsettings.UAT.json) | 60 req/min |
| Log file retention | None (console only) | 7 days | 30 days |
| Exchange rate sync (weekends) | Configurable | Enabled (`SkipWeekends=false`) | Disabled (`SkipWeekends=true`) |
| Secrets source | `dotnet user-secrets` | NTFS secrets.json (ADR-009) | NTFS secrets.json (ADR-009) |
| Azure Key Vault | No | No | No — pending ADR-006 (see Secrets section) |
| T21a QuestPDF license gate | N/A | Not required | **Required before first deploy** |
| ADR-008 DB2 write approval | N/A | Not required | **Required before first deploy** |

---

## Deploy Order (Critical)

```
1. Deploy Reporting Service to SRXWEBAPP1
2. Verify: GET https://srxwebapp1/reporting/api/v1/health → {"status":"Healthy"}
3. Deploy SM-Portal
4. Verify: /reports route in SM-Portal loads data
```

SM-Portal's `ReportingController` proxies all report requests to this service. If Reporting Service is not healthy when SM-Portal starts, SM-Portal returns 502/503 during its deployment window.

---

## Runbooks

| Runbook | Purpose |
|---|---|
| [docs/runbooks/uat-deployment.md](runbooks/uat-deployment.md) | Complete step-by-step guide for deploying to UAT on SRXWEBAPP1 |
| [docs/runbooks/TROUBLESHOOTING.md](runbooks/TROUBLESHOOTING.md) | Diagnose runtime issues and IIS deployment problems |

Complete [ai/checklists/pre-deployment.md](../ai/checklists/pre-deployment.md) before any server deployment.

---

## Quick-Reference Commands

### Build and Publish

```powershell
# From project root
dotnet build Reporting.Service.sln -c Release
dotnet test --collect:"XPlat Code Coverage"
dotnet publish src/Reporting.Api -c Release -o publish/
```

### IIS App Pool Management

```powershell
# Always confirm the pool name before stopping — do not assume
& "$env:windir\system32\inetsrv\appcmd.exe" list app "Default Web Site/reporting"

# Stop (before copying files)
& "$env:windir\system32\inetsrv\appcmd.exe" stop apppool /apppool.name:"ReportingService"

# Start (after copying files and editing web.config)
& "$env:windir\system32\inetsrv\appcmd.exe" start apppool /apppool.name:"ReportingService"

# Verify settings (No Managed Code, idle timeout 0, Integrated pipeline)
& "$env:windir\system32\inetsrv\appcmd.exe" list apppool "ReportingService" /text:*

# Fix: set idle timeout to 0 (required for 23:00 UTC exchange rate sync)
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool `
    /apppool.name:"ReportingService" /processModel.idleTimeout:00:00:00

# Fix: set managed runtime to No Managed Code
& "$env:windir\system32\inetsrv\appcmd.exe" set apppool `
    /apppool.name:"ReportingService" /managedRuntimeVersion:""
```

### Secrets Setup (Run as Admin on SRXWEBAPP1)

```powershell
# Create or update secrets file with NTFS ACLs
.\scripts\Setup-ServerSecrets.ps1

# Verify ACLs
Get-Acl "C:\ProgramData\SRX\Reporting\secrets.json" | Format-List

# Generate new API keys (on developer machine)
.\scripts\generate-api-key-simple.ps1
```

### Smoke Tests

```powershell
# Health (no key required)
curl -s https://srxwebapp1/reporting/api/v1/health

# Data sources
curl -s -H "X-API-Key: <key>" https://srxwebapp1/reporting/api/v1/health/data-sources

# Report catalog
curl -s -H "X-API-Key: <key>" "https://srxwebapp1/reporting/api/v1/reports?domain=CostManagement"

# Exchange rate (Production verification)
curl -s -H "X-API-Key: <key>" https://srxwebapp1/reporting/api/v1/exchange-rates/USD/2026-03-20
```

### IIS URL Rewrite Module Check (Required — HTTP→HTTPS redirect)

```powershell
Get-WebGlobalModule -Name "RewriteModule"
# Must return a result. If not installed, download URL Rewrite Module 2.1 from Microsoft.
```

---

## Secrets Management (ADR-009)

**Current approach (approved):** NTFS-protected JSON file at `C:\ProgramData\SRX\Reporting\secrets.json`.

This file:
- Lives **outside** the deployment folder — survives every redeployment without re-entry
- Has NTFS ACLs: `IIS AppPool\ReportingService` (Read), `Administrators` (FullControl), `SYSTEM` (FullControl) — all other accounts explicitly denied
- Is NOT in source control (covered by `**/secrets.json` in `.gitignore`)
- Has its **path** (not its contents) referenced via `REPORTING_SECRETS_PATH` in `web.config`

Keys stored in the file:

```json
{
  "ApiKeys": { "Primary": "...", "Admin": "..." },
  "DataSources": { "Db2": { "ConnectionString": "..." } }
}
```

Setup: run `scripts\Setup-ServerSecrets.ps1` as Administrator on SRXWEBAPP1.

**This is NOT Azure Key Vault.** The README and earlier versions of this file referenced Azure Key Vault as the production secrets mechanism. That is incorrect. ADR-006 (Azure Key Vault) is pending IT confirmation of availability on SRXWEBAPP1. ADR-009 (this NTFS file approach) is the approved interim approach and is in production use.

**Future upgrade path to Azure Key Vault (when ADR-006 prerequisites are met):**
1. Remove the `AddJsonFile(secretsFilePath)` block from `Program.cs`
2. Add `builder.Configuration.AddAzureKeyVault(...)` — same key names work unchanged
3. Upload secrets with double-hyphen notation: `ApiKeys--Primary`, `ApiKeys--Admin`, `DataSources--Db2--ConnectionString`
4. Delete `C:\ProgramData\SRX\Reporting\secrets.json` from the server
5. Remove `REPORTING_SECRETS_PATH` env var from `web.config`

---

## Rollback

**Retention policy:** Keep previous publish folder for a minimum of 1 week after each deployment.
**Naming convention:** `ReportingService_YYYYMMDD_HHmm` (e.g., `ReportingService_20260320_1430`)

```powershell
# Stop the pool
& "$env:windir\system32\inetsrv\appcmd.exe" stop apppool /apppool.name:"ReportingService"

# Swap physical path to previous backup
& "$env:windir\system32\inetsrv\appcmd.exe" set app "Default Web Site/reporting" `
    /physicalPath:"C:\Deployments\Reporting\ReportingService_YYYYMMDD_HHmm"

# Start pool and verify
& "$env:windir\system32\inetsrv\appcmd.exe" start apppool /apppool.name:"ReportingService"
curl -s https://srxwebapp1/reporting/api/v1/health
```

---

## Governance Gates by Environment

| Gate | UAT | Production | Reference |
|---|---|---|---|
| WR-3 README complete | Required | Required | README.md |
| WR-4 Swagger disabled | N/A (UAT enables it intentionally) | **Required** | web.config `ASPNETCORE_ENVIRONMENT=Production` |
| WR-5 HTTPS binding | Required | Required | IIS site bindings |
| WR-6 Runbooks in `docs/runbooks/` | Required | Required | This file ✓ |
| ADR-009 Secrets file in place | Required | Required | `scripts\Setup-ServerSecrets.ps1` |
| T21a QuestPDF license | Not required | **Required** | `ai/memory/08-governance-and-decisions.md` |
| ADR-008 DB2 CCURRA write approval | Not required | **Required** | `ai/memory/08-governance-and-decisions.md` |
| WR-1 Audit logging exception | Not required | **Required** | `ai/memory/08-governance-and-decisions.md` |
| ADR-006 Azure Key Vault | Not required | **Required** | Pending IT confirmation |
