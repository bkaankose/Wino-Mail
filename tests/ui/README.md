# Wino Mail UI tests

Double-click `Run-WinoUiTests.cmd` to stop Wino Mail, build the x64 Debug project, deploy it, and run every UI scenario.

The command file pauses at the end. A direct PowerShell run does not pause.
The runner leaves Wino Mail open. It writes screenshots and `test-results.json` under `artifacts/ui-tests/<timestamp>`.

For a faster deployment of the existing Debug output:

```powershell
.\tests\ui\Run-WinoUiTests.ps1 -NoBuild -Fast
```

The default path stops all `Wino.Mail.WinUI` processes before deployment.
This includes a process that has no visible window because close-to-tray is enabled.

Use an existing process only when current-source proof is not required:

```powershell
.\tests\ui\Run-WinoUiTests.ps1 -UseRunning -Scenario StartupSmoke -Fast
```

List or select scenarios without changing the test files:

```powershell
.\tests\ui\Run-WinoUiTests.ps1 -List
.\tests\ui\Run-WinoUiTests.ps1 -Scenario ComposeNavigation,MailRenderingNavigation -Fast
```

Use `-Pause` for a direct PowerShell run when you want to inspect the final state.
