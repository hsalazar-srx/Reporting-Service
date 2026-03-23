<#
.SYNOPSIS
    Creates and secures the Reporting Service secrets file on the IIS server.

.DESCRIPTION
    Replaces the insecure practice of storing secrets in web.config environment variables.
    Creates C:\ProgramData\SRX\Reporting\secrets.json with NTFS ACLs that restrict
    read access to the IIS app pool identity only.

    The secrets file is stored OUTSIDE the deployment folder so it survives redeployments
    without needing to be re-entered.

    Upgrade path: When Azure Key Vault is provisioned, remove the AddJsonFile call in
    Program.cs and replace it with AddAzureKeyVault(). The secrets file can then be deleted.

.PARAMETER AppPoolName
    IIS application pool name. Default: ReportingService

.PARAMETER SecretsPath
    Full path where secrets.json will be created.
    Default: C:\ProgramData\SRX\Reporting\secrets.json

.EXAMPLE
    .\Setup-ServerSecrets.ps1
    .\Setup-ServerSecrets.ps1 -AppPoolName "ReportingService" -SecretsPath "C:\ProgramData\SRX\Reporting\secrets.json"

.NOTES
    Run as Administrator on SRXWEBAPP1.
    Required before first production deployment.
#>
[CmdletBinding()]
param(
    [string]$AppPoolName = "ReportingService",
    [string]$SecretsPath = "C:\ProgramData\SRX\Reporting\secrets.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Require admin ──────────────────────────────────────────────────────────────
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator."
    exit 1
}

Write-Host ""
Write-Host "=== Reporting Service — Server Secrets Setup ===" -ForegroundColor Cyan
Write-Host "Secrets file: $SecretsPath"
Write-Host "App pool:     $AppPoolName"
Write-Host ""

# ── 1. Create directory ────────────────────────────────────────────────────────
$dir = Split-Path $SecretsPath -Parent
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Write-Host "[OK] Created directory: $dir" -ForegroundColor Green
} else {
    Write-Host "[OK] Directory exists:  $dir" -ForegroundColor DarkGreen
}

# ── 2. Prompt for secrets ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "Enter the secrets for this environment." -ForegroundColor Yellow
Write-Host "Values are masked. Press Enter to keep existing value (if file already exists)."
Write-Host ""

function Read-SecretValue([string]$Prompt, [string]$ExistingValue = "") {
    $masked = if ($ExistingValue) { " [currently set — Enter to keep]" } else { " [required]" }
    $input = Read-Host "$Prompt$masked"
    if ([string]::IsNullOrWhiteSpace($input)) {
        return $ExistingValue
    }
    return $input.Trim()
}

# Load existing values if file exists (so operator can update only what changed)
$existing = @{ ApiKeys = @{ Primary = ""; Admin = "" }; DataSources = @{ Db2 = @{ ConnectionString = "" } } }
if (Test-Path $SecretsPath) {
    try {
        $existing = Get-Content $SecretsPath | ConvertFrom-Json -AsHashtable
        Write-Host "[INFO] Existing secrets file found — will update." -ForegroundColor DarkYellow
    } catch {
        Write-Host "[WARN] Could not parse existing file — will overwrite." -ForegroundColor Yellow
    }
}

$primaryKey   = Read-SecretValue "ApiKeys:Primary  (API key for SM-Portal / external callers)" `
    ($existing.ApiKeys?.Primary ?? "")
$adminKey     = Read-SecretValue "ApiKeys:Admin    (Admin API key for management operations)" `
    ($existing.ApiKeys?.Admin ?? "")
$db2ConnStr   = Read-SecretValue "DataSources:Db2:ConnectionString  (ODBC connection string)" `
    ($existing.DataSources?.Db2?.ConnectionString ?? "")

# Validate
$missing = @()
if (-not $primaryKey)  { $missing += "ApiKeys:Primary" }
if (-not $adminKey)    { $missing += "ApiKeys:Admin" }
if (-not $db2ConnStr)  { $missing += "DataSources:Db2:ConnectionString" }

if ($missing.Count -gt 0) {
    Write-Error "Missing required secrets: $($missing -join ', '). Cannot continue."
    exit 1
}

# ── 3. Write secrets file ──────────────────────────────────────────────────────
$secrets = [ordered]@{
    ApiKeys    = [ordered]@{
        Primary = $primaryKey
        Admin   = $adminKey
    }
    DataSources = [ordered]@{
        Db2 = [ordered]@{
            ConnectionString = $db2ConnStr
        }
    }
}

$secrets | ConvertTo-Json -Depth 5 | Set-Content -Path $SecretsPath -Encoding UTF8 -Force
Write-Host "[OK] Secrets file written: $SecretsPath" -ForegroundColor Green

# ── 4. Set NTFS ACLs ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Applying NTFS ACLs..." -ForegroundColor Cyan

$acl = New-Object System.Security.AccessControl.FileSecurity

# Disable inheritance — start from scratch (no inherited permissions)
$acl.SetAccessRuleProtection($true, $false)

# Administrators: Full Control
$adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "Administrators",
    [System.Security.AccessControl.FileSystemRights]::FullControl,
    [System.Security.AccessControl.InheritanceFlags]::None,
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Allow)
$acl.AddAccessRule($adminRule)

# SYSTEM: Full Control (required for Windows OS operations)
$systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "SYSTEM",
    [System.Security.AccessControl.FileSystemRights]::FullControl,
    [System.Security.AccessControl.InheritanceFlags]::None,
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Allow)
$acl.AddAccessRule($systemRule)

# IIS App Pool identity: Read only
$poolIdentity = "IIS AppPool\$AppPoolName"
try {
    $poolRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $poolIdentity,
        [System.Security.AccessControl.FileSystemRights]::Read,
        [System.Security.AccessControl.InheritanceFlags]::None,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($poolRule)
    Write-Host "[OK] Read access granted to: $poolIdentity" -ForegroundColor Green
} catch {
    Write-Warning "Could not add ACL for '$poolIdentity' — verify app pool name is correct."
    Write-Warning "You may need to grant Read manually via File Explorer > Properties > Security."
}

Set-Acl -Path $SecretsPath -AclObject $acl
Write-Host "[OK] ACLs applied. Inheritance disabled." -ForegroundColor Green

# ── 5. Verify ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Verifying..." -ForegroundColor Cyan

$verifyAcl = Get-Acl $SecretsPath
Write-Host "Effective permissions on $SecretsPath :"
$verifyAcl.Access | Format-Table IdentityReference, FileSystemRights, AccessControlType -AutoSize

# Confirm file is readable
try {
    $content = Get-Content $SecretsPath | ConvertFrom-Json
    Write-Host "[OK] File is valid JSON and readable by current session." -ForegroundColor Green
} catch {
    Write-Error "File could not be read or parsed: $_"
    exit 1
}

# ── 6. Summary ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Secrets stored at:  $SecretsPath"
Write-Host "Loaded via:         REPORTING_SECRETS_PATH env var in web.config"
Write-Host ""
Write-Host "Keys configured:"
Write-Host "  ApiKeys:Primary                       [set]"
Write-Host "  ApiKeys:Admin                         [set]"
Write-Host "  DataSources:Db2:ConnectionString      [set]"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Restart the ReportingService app pool:"
Write-Host "     & `"`$env:windir\system32\inetsrv\appcmd.exe`" recycle apppool /apppool.name:`"$AppPoolName`""
Write-Host ""
Write-Host "  2. Smoke test health endpoint:"
Write-Host "     curl -s -H `"X-API-Key: <primary-key>`" https://srxwebapp1/reporting/api/v1/health"
Write-Host ""
Write-Host "  3. When Azure Key Vault is available (ADR-006):"
Write-Host "     - Remove AddJsonFile block from Program.cs"
Write-Host "     - Add AddAzureKeyVault() with the same key names"
Write-Host "     - Delete this secrets file"
Write-Host ""
