# Scripted UI regression tooling

`Run-WinoRegression.ps1` and `Regression.Common.psm1` implement the finite task,
contact, and activation regression scenarios described in `REGRESSION.md`.
Keep their exact command recording, package identity checks, synthetic record
tracking, and failure recovery intact. Do not treat a CLI action as a passing
assertion without checking the resulting UI state.
