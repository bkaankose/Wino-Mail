# UI audit replay tooling

For `Run-WinoRegression.ps1`, read REGRESSION.md. It automates a finite subset with its own scenario names.
Keep the full baseline replay and scripted regression results distinct. Deployment conflicts must stop before app interaction.

When replaying the Contacts / People, To Do, and activation audit, read REPLAY.md first. Every WinApp CLI command, including queries, focus, retries, deployment and screenshots, must use Invoke-WinoRecorded.ps1 with an argument array. Create a fresh run with New-WinoAuditRun.ps1; preserve per-scenario records/results and never copy baseline outcomes as fresh passes. Generated HWND/element selectors must be resolved from current UI. This is agent-guided tooling, not a certified unattended suite. Stop on user pause and reconcile in-flight actions before resuming. Do not modify application code to make tests pass.
