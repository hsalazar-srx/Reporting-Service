# Setup Git Hooks — Reporting Service
# Run once after cloning: .\setup-hooks.ps1

$hooksDir = ".git/hooks"
$preCommitPath = "$hooksDir/pre-commit"

if (-not (Test-Path $hooksDir)) {
    Write-Error "Not a git repository. Run 'git init' first."
    exit 1
}

$hookContent = @'
#!/bin/sh
# Pre-commit hook — Reporting Service

# Check 1: 00-skills-audit.md must exist when src/ files are staged
staged_src=$(git diff --cached --name-only | grep "^src/")
if [ -n "$staged_src" ]; then
    if [ ! -f "ai/memory/00-skills-audit.md" ]; then
        echo "ERROR: Pre-commit hook blocked commit."
        echo "ai/memory/00-skills-audit.md is MISSING."
        echo "Complete skills audit before committing implementation files."
        exit 1
    fi
fi

echo "Pre-commit checks passed."
exit 0
'@

# Fix: Use UTF8 without BOM + LF line endings (not CRLF)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$hookContentLF = $hookContent -replace "`r`n", "`n"
[System.IO.File]::WriteAllText(
    (Resolve-Path $hooksDir).Path + "/pre-commit",
    $hookContentLF,
    $utf8NoBom
)

# Make executable (for Git Bash / WSL)
if (Get-Command git -ErrorAction SilentlyContinue) {
    git update-index --chmod=+x $preCommitPath 2>$null
}

Write-Host "Pre-commit hook installed at $preCommitPath" -ForegroundColor Green
Write-Host "Hook will block src/ commits if ai/memory/00-skills-audit.md is missing." -ForegroundColor Yellow
