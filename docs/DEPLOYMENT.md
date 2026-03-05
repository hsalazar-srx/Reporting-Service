# Deployment Guide — Reporting Service

## Target Server

**SRXWEBAPP1** — IIS with ASP.NET Core Module v2

## Deploy Order (CRITICAL)

```
1. Deploy Reporting Service to SRXWEBAPP1 FIRST
2. Verify: GET https://srxwebapp1/reporting/api/v1/health → 200 OK
3. Deploy SM-Portal changes (ReportingController proxy) SECOND
4. Verify: /reports route in SM-Portal
```

## Pre-Deployment Checklist

Complete [ai/checklists/pre-deployment.md](../ai/checklists/pre-deployment.md) before every deployment.

## IIS Configuration

1. Create Application Pool: `ReportingService` (.NET CLR: No Managed Code, Pipeline: Integrated)
2. Create Application under Default Web Site: `/reporting` pointing to publish folder
3. Configure HTTPS binding (SSL certificate required — WR-5 compliance)
4. Ensure IBM i ODBC DSN is configured for the App Pool identity

## Publish Command

```bash
dotnet publish src/Reporting.Api -c Release -o publish/
```

## Azure Key Vault (Production — WR-2)

Ensure `AzureKeyVault:VaultUri` is set in `appsettings.json` and the App Pool identity
has `Get` and `List` permissions on the Key Vault secrets.

Required secrets in Key Vault:
- `ApiKeys--Primary`
- `ApiKeys--Admin`
- `DataSources--Db2--Password`
- `DataSources--SqlServer--ConnectionString`

## Rollback

Keep previous publish folder accessible for 1 week. IIS — swap Application physical path.
