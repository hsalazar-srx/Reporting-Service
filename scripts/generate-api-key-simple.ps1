# Simple script: generates a Base64 256-bit API key and stores it in User Secrets under ApiKeys:Primary
# No parameters; edit SecretName or ProjectPath inline if needed.

$ErrorActionPreference = 'Stop'

$ProjectPath = Join-Path (Join-Path $PSScriptRoot '..') '/src/Reporting.Api/Reporting.Api.csproj'
if (-not (Test-Path -LiteralPath $ProjectPath)) {
    Write-Error "Project file not found: $ProjectPath"
    exit 1
}

$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)

Write-Host "Setting ApiKeys:Primary for project: $ProjectPath" -ForegroundColor Cyan
$proc = Start-Process -FilePath dotnet -ArgumentList @('user-secrets','set','ApiKeys:Primary',$key,'--project',$ProjectPath) -NoNewWindow -PassThru -Wait -RedirectStandardOutput out.txt -RedirectStandardError err.txt
if ($proc.ExitCode -ne 0) {
    Write-Error (Get-Content -Raw err.txt)
    exit $proc.ExitCode
}
Remove-Item out.txt, err.txt -ErrorAction SilentlyContinue

Write-Host "Stored successfully." -ForegroundColor Green
Write-Host "Key value:" -ForegroundColor DarkGray
Write-Host $key

Write-Host "Listing secrets:" -ForegroundColor Cyan
& dotnet user-secrets list --project $ProjectPath

Write-Host "Use header: X-API-Key: $key" -ForegroundColor Yellow
