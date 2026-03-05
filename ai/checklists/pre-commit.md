# Pre-Commit Checklist

Run before every `git commit`:

```
[ ] Code compiles without errors: dotnet build --no-restore
[ ] All tests pass: dotnet test --no-build
[ ] Code coverage ≥80% (Core + Infrastructure)
[ ] No hardcoded secrets/credentials/connection strings
[ ] No PII in log messages or error responses
[ ] C# naming conventions followed (PascalCase classes, I prefix interfaces)
[ ] All public methods have XML documentation comments (<summary>)
[ ] Skills referenced in code comments where applicable
[ ] Commit message follows Conventional Commits: feat:, fix:, docs:, chore:, test:, refactor:
[ ] Commit references sprint task (e.g., "feat: add JsonReportCatalogProvider (T3)")
[ ] 00-skills-audit.md exists (pre-commit hook enforces this)
[ ] No TODO comments without task reference (e.g., // TODO T23: async polling)
```
