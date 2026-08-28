# Wino Mail UI tests

Double-click `Run-WinoUiTests.cmd` to build the checked-in x64 Debug project, launch the existing Debug package identity, and run every `*.UiTest.ps1` in `Tests`.

The runner leaves Wino Mail open and pauses at the end so the final UI state can be inspected. Test screenshots and `test-results.json` are written below `artifacts/ui-tests/<timestamp>`.

For a faster rerun against an already deployed Debug build:

```powershell
.\tests\ui\Run-WinoUiTests.ps1 -NoBuild -Fast
```

Close Wino Mail before a run when the source was changed and the Debug package must be rebuilt and redeployed.
