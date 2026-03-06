# Setup Git Hooks — Reporting Service
# Run once after cloning: .\setup-hooks.ps1
#
# This script activates the versioned .githooks/ folder via git config core.hooksPath.
# DO NOT write hook scripts inline here — edit .githooks/pre-commit directly instead.

Write-Host "Initializing Skills-First Architecture Enforcement" -ForegroundColor Cyan
Write-Host ""

# Check if .git exists
if (-not (Test-Path ".git")) {
    Write-Error "Not a git repository. Run 'git init' first."
    exit 1
}

Write-Host "Configuring Git hooks path..." -ForegroundColor Cyan

# Point Git at the versioned .githooks/ folder (not the ephemeral .git/hooks/)
git config core.hooksPath .githooks

$hooksPath = git config --get core.hooksPath
if ($hooksPath -eq ".githooks") {
    Write-Host "OK: Git hooks path configured: .githooks" -ForegroundColor Green
} else {
    Write-Host "ERROR: Failed to configure hooks path" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Checking skills audit..." -ForegroundColor Cyan

if (Test-Path "ai/memory/00-skills-audit.md") {
    Write-Host "OK: Skills audit exists: ai/memory/00-skills-audit.md" -ForegroundColor Green
} else {
    Write-Host "WARNING: Skills audit not found" -ForegroundColor Yellow
    if (Test-Path "ai/memory/00-skills-audit-template.md") {
        $createAudit = Read-Host "Create from template? (y/N)"
        if ($createAudit -eq "y" -or $createAudit -eq "Y") {
            Copy-Item "ai/memory/00-skills-audit-template.md" "ai/memory/00-skills-audit.md"
            Write-Host "OK: Created ai/memory/00-skills-audit.md from template" -ForegroundColor Green
            Write-Host ""
            Write-Host "TODO: Complete the skills audit before creating implementation files" -ForegroundColor Yellow
            Write-Host "  1. Review C:\Projects\.github\skills\manifest.json" -ForegroundColor White
            Write-Host "  2. Review C:\Projects\.github\agents\manifest.json" -ForegroundColor White
            Write-Host "  3. Fill out ai/memory/00-skills-audit.md with actual project info" -ForegroundColor White
        }
    } else {
        Write-Host "  Create ai/memory/00-skills-audit.md manually before committing implementation files." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Checking hook file..." -ForegroundColor Cyan

if (Test-Path ".githooks/pre-commit") {
    Write-Host "OK: Pre-commit hook found: .githooks/pre-commit" -ForegroundColor Green
    if ($IsLinux -or $IsMacOS) {
        chmod +x .githooks/pre-commit
        Write-Host "OK: Hook marked executable" -ForegroundColor Green
    }
} else {
    Write-Host "ERROR: .githooks/pre-commit not found." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Setup complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Complete ai/memory/00-skills-audit.md (if not done)" -ForegroundColor White
Write-Host "  2. Create implementation files (src/, Services/, etc.)" -ForegroundColor White
Write-Host "  3. Commit - hook will validate skills audit exists" -ForegroundColor White
Write-Host ""
Write-Host "To verify: git config core.hooksPath  (should print: .githooks)" -ForegroundColor Gray
