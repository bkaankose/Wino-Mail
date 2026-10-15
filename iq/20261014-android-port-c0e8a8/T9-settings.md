# T9 Settings, dialogs, theme service (done as infrastructure, 14 Oct 2026)

Visuals await the final boards. All sizes, colours and text styles come from Presentation.Android resources (`wino_scale.xml`, `wino_text.xml`, `wino_*colors/tokens*.xml`) through `WinoTokens`.

## Files
- **Head `Views/Settings/`:**
  - `SettingsHostFragment`: compact shows one page at a time; w600dp shows the list beside the page and opens on General.
  - `SettingsRootFragment`, `SettingsPageFragment` (the iOS base members), `SettingsPageRoutes(.<Group>).cs`.
  - Pages in `Pages/`, `Personalization/`, `GeneralAndMail/`, `CalendarContactsToDo/`, `Accounts/`, `WinoAccount/`.
  - Debug only: `SettingsDebugActivity` (`com.winomail.android.SettingsDebug --es command page|accountpage|dialog|theme|mode|accent --es arg X`).
- **Navigation:**
  - The Settings stack always has the root list at the bottom.
  - New `SelectSettingsRootPageAsync`; it also handles `SettingsRootNavigationRequested`.
  - Back asks `IConfirmBackNavigation` first.
  - New route `Initialize`/`Deactivate` hooks (the iOS `InitializeAsync`/`DeactivateAsync`).
- **Dialogs:** `AndroidFormDialog`, `AndroidDialogService.Forms.cs` and `.WinoAccount.cs`; the service is now partial. `Views/Dialogs/WinoHtmlTextEditor`.
- **Controls:**
  - `SettingsBinder` adds Checkbox, PopUp with items property, Stepper, Segmented, TextField, TimePicker (MaterialTimePicker), Slider and Label styles.
  - New `WinoChoiceSheet` (modal bottom sheet for pickers), `WinoMaterialAccent` and `WinoTokens`.
  - `WinoSettingsCard`/`Expander`/`Group` now read tokens; the group subheader uses the primary colour.

## Pages
All 34 iOS routes are registered and bound to their shared ViewModels. Opened on the phone: every root page and all account sub-pages except Merged account details (needs a linked inbox); Intelligence management and Coverage need the entitlement.

Hidden through capabilities, not head rules:
- Companion: `Tray`.
- Keyboard shortcuts: new `KeyboardShortcuts` capability.
- Backup and restore: new `FileBackup` capability.
- S/MIME.
- Purchases: new `Purchases` capability. The shared ViewModels now hide the purchase panel and Buy buttons, and at the account limit they only explain it.

## Dialogs
- Real: alias, category, system folders, signature, account order (move up/down), Wino Account sign-in (with forgot password and the confirmation dialog), registration, and the sync secret (unlock and new backup).
- Each one was opened on the phone and cancelled. No real sign-in was attempted.
- Not built:
  - Keyboard shortcut: answers Cancel, because the capability is off.
  - Purchase channel: answers null.
  - File and folder pickers: left to T8, whose `AndroidDocumentPicker` adds `PickFilesAsync`. Wire `PickWindowsFileContentAsync` and `PickFilesMetadataAsync` to it at integration. The theme wallpaper and profile pictures need them.

## Theme service
`AndroidThemeService`: predefined, gradient and custom themes (save, delete, JPEG previews), editor preview and restore, accent (`WinoColors.AccentOverride` plus Material tints), light/dark/system, backdrop switch, icon style. An accent or icon change recreates the activity (pages keep state). Checked live: Forest, Coastline, Nebula, a custom accent, dark mode.

## Shared moves
- `SettingsCatalog` (Domain) replaces the iOS head catalogue; the Mac catalogue is now a thin adapter.
- `SettingsPageAvailability` gains a capabilities overload.
- `NotificationSettingsPageViewModel.ConfirmResetAsync` and `EmailTemplatesPageViewModel.DeleteTemplateAsync`; the Mac, iOS and Android pages use them.
- `Purchases` gating.
- New tests in Core (catalogue) and ViewModels (no purchases).

## Verification
- Android Debug: 0 errors, no CS/CA warnings.
- Core tests: 1217 passed, 5 known failures. ViewModels 210/210, Controls 89/89.
- Mac Debug arm64 and x64, and iOS Simulator Debug: 0 errors.
- Phone (16 KB) and tablet: `T9-*.png`.

## Gaps
- Visual design: pending the final boards.
- Theme editor surface preview drawings are omitted.
- Some long segment labels are truncated.
- Two strings were added: `Android_Settings_Decrease` and `Android_Settings_Increase`.
- Copied head rules still to move: signature preview text and filter summaries (Accounts).
- `.gitignore` `[Dd]ebug/` excludes `src/Wino.Mail.Android/Views/Debug/` (the T6/T7 debug activities are untracked).
- Windows build and Release gate: owner handoff.
