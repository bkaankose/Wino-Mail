# Scripted Wino UI regression

Run the finite task, contact, and activation checks through `./scripts/wino.ps1 audit app`.
See [REGRESSION.md](REGRESSION.md) for scenarios, prerequisites, evidence, and recovery.

`Run-WinoRegression.ps1` deploys current Debug source once and records each WinApp command.
`New-WinoRegressionRun.ps1` creates a unique run directory, and `Invoke-WinoRecorded.ps1`
records exact argument arrays, output, and exit codes. `Test-Recorder.ps1` checks the
recorder without interacting with Wino Mail.

The separate unattended UI suite is in [../../tests/ui](../../tests/ui/README.md).
