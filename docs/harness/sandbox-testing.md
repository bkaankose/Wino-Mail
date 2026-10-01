# Windows Sandbox testing

Use this procedure only when the user explicitly requests Windows Sandbox testing.
Local deployment and local UI tests remain the default.

## Requirements

Use WinApp CLI 0.7.0 or later for this procedure.
Windows Sandbox requires Windows 11 24H2 or later, a supported edition, hardware virtualization, and the enabled Sandbox feature.
The host session must remain unlocked for input and capture.

WinApp builds on the host, then deploys and activates the package inside the guest.
The same guest persists between commands until Windows Sandbox stops.
Guest accounts and application data are separate from host data.

Reference: [Microsoft Sandbox execution documentation](https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/sandbox-execution).

## Plan and deployment

1. Run `./scripts/wino.ps1 doctor app` before runtime work.
   The host report does not establish guest package ownership.
2. Run `winapp target snapshot sandbox --json` to inspect the current guest without starting it.
3. Prepare the guest and inspect its package registration:

   ```powershell
   $env:WINAPP_UI_WORKFLOW_ID = 'wino-sandbox-test-01'
   winapp target exec sandbox -- powershell.exe -NoProfile -Command "Get-AppxPackage -Name '58272BurakKSE.WinoMailPreview' | Select-Object Name,Publisher,IsDevelopmentMode,SignatureKind | ConvertTo-Json"
   ```

4. Compare guest package name and publisher with `src/Wino.Mail.WinUI/Package.appxmanifest`.
   A fresh guest can have no matching package.
   Stop on a publisher mismatch or an incompatible signed registration.
5. Immediately before deployment, stop existing Debug app processes inside the guest:

   ```powershell
   winapp target exec sandbox -- powershell.exe -NoProfile -Command "Get-Process -Name 'Wino.Mail.WinUI' -ErrorAction SilentlyContinue | Stop-Process -Force"
   ```

6. Build and deploy the current Debug x64 source through project mode:

   ```powershell
   winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --on sandbox -c Debug -r win-x64 --no-restore -p Platform=x64 -p GenerateAppxPackageOnBuild=false -p AppxPackageSigningEnabled=false --detach --json
   ```

   Restore first when the development guide requires it.
   Use `--no-build` only after a successful build with unchanged source and dependencies.
7. Check that the result reports `Sandbox: true`, `ProcessScope: sandbox`, and a guest `ProcessId`.
   Preserve the deployment result and its `UiTargetArgs` as evidence.

Preserve the checked-in identity and application data.
Use Debug project mode for every deployment.
Never use folder mode, `--clean`, `--unregister-on-exit`, or Release runtime tests.
Apply the process shutdown to the guest only.

## UI smoke test

Set the same `WINAPP_UI_WORKFLOW_ID` in each shell invocation for this test.
Keep `--on sandbox` on every guest UI command, including commands that use a PID or HWND.

1. Replace `<guest-pid>` with the deployment result:

   ```powershell
   winapp ui list-windows --on sandbox -a <guest-pid> --json
   winapp ui inspect --on sandbox -a <guest-pid> --interactive --depth 8 --json
   ```

2. Select an unambiguous app window from the result.
   Use its guest HWND with `-w <guest-hwnd>` for subsequent commands.
3. On a fresh welcome screen, assert and invoke the Get Started button:

   ```powershell
   winapp ui wait-for WelcomePageV2Button --on sandbox -w <guest-hwnd> --timeout 5000
   winapp ui invoke WelcomePageV2Button --on sandbox -w <guest-hwnd>
   winapp ui wait-for WelcomePageV2Button --gone --on sandbox -w <guest-hwnd> --timeout 5000
   winapp ui wait-for ProviderSelectionFeaturedProviders --on sandbox -w <guest-hwnd> --timeout 5000
   winapp ui inspect --on sandbox -w <guest-hwnd> --interactive --depth 8 --json
   ```

4. Check the resulting provider-selection page against its current XAML AutomationIds.
   The `ProviderSelectionFeaturedProviders` assertion establishes the expected destination.
   If the guest already has accounts, select a reversible navigation test from its current UI tree.
5. Capture the tested app window:

   ```powershell
   winapp ui screenshot --on sandbox -w <guest-hwnd> -o artifacts/wino-sandbox-smoke.png --json
   winapp ui yield --on sandbox
   ```

6. Inspect the screenshot for clipping, overlap, and the visible theme.
7. Report the deployment result, action, state assertion, guest PID/HWND, theme, and host evidence paths.

A screenshot or a successful invoke alone does not establish a passing test.
Rediscover guest PIDs and HWNDs after the Sandbox restarts.
Configure test accounts explicitly when a scenario requires them.
Keep evidence on the host before the guest stops.
Stop the Sandbox only when the user authorizes discarding its session.

## Trial: 2026-10-01

- WinApp CLI version: `0.7.0`.
- Host doctor: `Ready`, matching publisher, development-mode Debug registration.
- Initial snapshot: no running Sandbox.
- Guest preparation started Sandbox, then failed during the `wsb share` operation.
- A retry failed with the same `0x80070005 (E_ACCESSDENIED)` error.
- `winapp ui status --on sandbox -a Wino.Mail.WinUI --json` reported `sandbox_start_failed`.
- Its error context identified `wsbVerb: share`, `exitCode: -2147024891`, and `hresult: 0x80070005`.
- Snapshot showed a running guest, no attached interactive session, and no deployments.
- The project-mode Debug x64 build succeeded with zero warnings and zero errors.
- Deployment then failed with `sandbox_start_failed` at the same `wsb share` operation.
- UI interaction, theme, and screenshots remain unverified.

WinApp recommended a retry, then a host restart if the error persisted.
The retry did not resolve the error.
No host restart, package cleanup, or Sandbox shutdown occurred.
After a user-controlled host restart, repeat guest preparation before deployment.
If the same error persists, investigate Windows Sandbox folder-sharing access before app changes.

## Retry against the existing guest: 2026-10-01

`wsb list` returned `4a474bd3-d75e-477c-ba8e-cce2ae7bcf4b`.
WinApp recognized and reused that guest.
The project-mode deployment retry failed during internal guest preparation.

An explicit transfer attempt used the target command:

```powershell
winapp target push sandbox src/Wino.Mail.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/AppX WinoMailDebug --json
```

This command also returned `sandbox_start_failed`, with `wsbVerb: share` and `hresult: 0x80070005`.
WinApp invokes `wsb share` internally before its target commands can operate.
No direct `wsb share` command was used for deployment.
The target transfer did not complete.
Guest package registration and UI tests remain blocked.
