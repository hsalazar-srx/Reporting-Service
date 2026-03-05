# Configuration Pattern

---

## appsettings.json Structure

```json
{
  "DataSources": {
    "Db2": {
      "Dsn": "MOVEX_PROD",
      "Schema": "mvxcdta",
      "DefaultTimeoutSeconds": 300
    },
    "SqlServer": {
      "ConnectionString": "{{FROM_USER_SECRETS}}",
      "DefaultTimeoutSeconds": 120
    }
  },
  "Catalog": {
    "FilePath": "config/report-catalog.json",
    "ReloadIntervalSeconds": 60
  },
  "ApiKeys": {
    "Primary": "{{FROM_USER_SECRETS}}",
    "Admin": "{{FROM_USER_SECRETS}}"
  },
  "RateLimiting": {
    "FixedWindow": {
      "PermitLimit": 60,
      "WindowSeconds": 60
    }
  },
  "Report": {
    "DefaultExecutionTimeoutSeconds": 300
  },
  "AzureKeyVault": {
    "VaultUri": "https://srx-keyvault.vault.azure.net/"
  },
  "Swagger": {
    "EnableUI": false
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [{ "Name": "Console" }],
    "Enrich": ["FromLogContext", "WithCorrelationId"]
  }
}
```

## Config Hierarchy (Highest → Lowest Priority)

1. Azure Key Vault (production) / Environment variables
2. `dotnet user-secrets` (development only)
3. `appsettings.{Environment}.json`
4. `appsettings.json`

## User Secrets Keys (Development)

```bash
dotnet user-secrets set "ApiKeys:Primary" "your-primary-key-here"
dotnet user-secrets set "ApiKeys:Admin" "your-admin-key-here"
dotnet user-secrets set "DataSources:Db2:Password" "your-db2-password"
dotnet user-secrets set "DataSources:SqlServer:ConnectionString" "Server=150.3.20.116;..."
```

## Never Commit

- `appsettings.Development.json` with real credentials
- `secrets.json`
- Any `.env` files with credentials
