# T2 Windows head: report

## Checkpoints

- 13 Oct 2026: started; read plan, T1 report.
- Added Wino.Platform.Windows/PushRelay (identity store, local JSON context, null token provider, device info); DI and capabilities in CoreUWPContainerSetup; expander in NotificationSettingsPage.xaml. Next: format and builds.
- format-xaml -Changed and -Check -Changed pass (repo-wide -Check reports 14 pre-existing failures in files this task did not touch). Debug x64 build succeeded, 0 warnings. Next: Release gate.
- Release x64 gate succeeded, 0 warnings, 0 errors. Done. Nothing committed.

## Files

Added (`src/Wino.Platform.Windows/PushRelay/`):
- `WindowsPushRelayDeviceIdentityStore.cs`: `<ApplicationDataFolderPath>\PushRelay\device-identity.json`, temp file + `File.Move(overwrite)` like `WindowsGoogleTokenStore.WriteAsync`, a `SemaphoreSlim` serializes load/save/delete. `LoadAsync` returns null when missing; deletes the file and returns null on `JsonException` or an identity with empty ids or platform.
- `WindowsPushRelayJsonContext.cs`: internal source-generated context for `PushRelayDeviceIdentity` with the same options as `PushRelayJsonContext` (camelCase, ignore nulls, case-insensitive), so the file format matches.
- `NullPushTokenProvider.cs`: no channel, null token and failure, empty event accessors.
- `WindowsPushRelayDeviceInfo.cs`: `PushRelayPlatforms.Windows`, `Environment.MachineName`, `"Windows " + Environment.OSVersion.Version`, each trimmed to 64 characters.

Changed:
- `src/Wino.Mail.WinUI/CoreUWPContainerSetup.cs`: registers the three singletons; capabilities `RemoteNotifications: false, PushRelayOptIn: true`.
- `src/Wino.Mail.WinUI/Views/Settings/NotificationSettingsPage.xaml`: "Live notifications" `SettingsExpander` after To-do reminders (Cloud icon, description, header `ToggleSwitch` bound two-way to `IsPushRelayEnabled` with `IsEnabled=IsPushRelayToggleEnabled`, `ItemsHeader` InfoBars for sign-in and mailbox warning, status card bound to `PushRelayStatusText`). All compiled `x:Bind`. No code-behind change, no new strings.

## Deviations

- `Wino.Platform.Windows` references only `Wino.Core.Domain` and `Wino.Authentication`, not `Wino.Services`, so it cannot use `PushRelayJsonContext`. Instead of adding a project reference, it has its own internal context for the one record.
- Push-test UI omitted on Windows (`IsPushTestVisible` is always false there).

## Verification

- `scripts/maintenance/windows/format-xaml.ps1 -Changed`: formatted 1 file. `-Check -Changed`: 1 of 1 pass. Plain `-Check` (whole repo) fails on 14 files that this task did not touch.
- `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64`: succeeded, 0 warnings.
- `dotnet build ... -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false`: succeeded, 0 warnings, 0 errors. No shared-code AOT/trim fixes were needed.

## Pending manual checks (not launched)

- Plan section 6 check 1: expander visible; toggle disabled until Wino sign-in with the sign-in InfoBar; after opt-in the token call succeeds, the relay rejects `windows`, status shows "not available on this device yet", no retry storm.
- `device-identity.json` written under the app data folder after a registration, and removed on Wino sign-out.
- Light/dark and narrow-width layout of the new expander.
