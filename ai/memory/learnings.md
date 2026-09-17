# Agent Learnings — Reporting-Service

Accumulated lessons from working in this project.
Read this file at the start of each session. Newest-first.

---

### [2026-08-18] IIS `startMode=OnDemand` means the exchange rate sync never starts
**Type:** pitfall | **Severity:** high
The RBA sync timer is created in `Program.cs` before `app.RunAsync()`, so it only exists while the
IIS worker is alive — and with the default `startMode=OnDemand` the worker only launches on the
first HTTP request. `idleTimeout=0` (prescribed in all three deployment docs) stops IIS *killing* a
running process but never *starts* one, so the sync ran only when someone happened to call the API.
Result: CCURRA had no rates for 2026-04-02 → 2026-08-17 (8 currencies × 93 dates). Detected via
gaps in Serilog rolling-file *filenames* — a health endpoint cannot report that its own process is
dead. Fix: `startMode=AlwaysRunning` + `preloadEnabled=true` + `idleTimeout=0`; verify with
`appcmd list wp` under zero traffic. Also raised `FallbackDays` 3 → 5 (AU public holidays create
4–5 day gaps in RBA data) and corrected an inverted rate-convention comment in four places — the
stored data was always right, only the docs were backwards.
See vault: vault/learnings/reporting/2026-08-18-iis-startmode-ondemand-means-scheduler-never-starts.md
