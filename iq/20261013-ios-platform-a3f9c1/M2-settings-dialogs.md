# M2: Settings pages, Wino Account sheets and dialogs (iOS, `src/Wino.Mail.UIKit`)

**Worktree:** `D:\Wino-Mail\.claude\worktrees\agent-a105e8c4c68a3deca` (branch `worktree-agent-a105e8c4c68a3deca`), seeded with `artifacts/ios-platform/patches/seed-after-J.patch` (staged by `git apply --3way`). All M2 work is untracked or unstaged on top. Nothing is committed.

**Mac mirror:** `~/Desktop/wt-M2`. The P1–P5 helper mirrors are `~/Desktop/wt-M2-P1` … `wt-M2-P5`; they were used only for compile checks and can be deleted.

**Result:**
- The iOS head builds in Debug for the simulator with 0 errors.
- On iPhone and iPad simulators, Settings opens as a sheet from Welcome (no account). The root list, ten pages and seven dialogs render; snapshots are listed below.
- Every J stub is replaced: no dialog shows the "unsupported" banner any more.

## Integration lines needed (outside my scope, not applied in the worktree)

1. **`src/Wino.Mail.UIKit/UIKitViewRegistration.cs`.** The only edit I made is `static class` → `static partial class`. The owner must add one call inside `RegisterUIKitViews`, after the onboarding controllers:
   ```csharp
   RegisterSettingsViews(services);
   ```
   The mirror build applied this line with `sed`. Without it, none of the Settings registrations run.
2. **Routes.** No edit to K's or L's registries is needed. `SettingsLifecycleHook` (an `IApplicationLifecycleHook`) adds every Settings route to the idiom registry when services are ready, through the public `UIKitPageRegistry.Register`:
   - the host is `FullScreenSheet` on iPhone and `Sheet` on iPad;
   - it skips this if the registry already maps `WinoPage.SettingsPage`.

   K or L can instead call `SettingsPageRoutes.Register(this, UIKitPageHost.FullScreenSheet)` (or `.Sheet`) in their registry constructor.
3. **`UIKitDialogService.cs` (J's file).** Changes are limited to:
   - `sealed class` → `sealed partial class`;
   - deleting the replaced stubs and `CancelledAliasDialog`;
   - moving `ShowWinoAccountSyncSecretDialogAsync` and `ShowServerCertificateTrustDialogAsync` (previously alerts) to sheets in `.WinoAccount.cs`.

## Files (76 files, about 13.5k lines; all under `src/Wino.Mail.UIKit/`)

- **Foundation (lead):**
  - `Views/Settings/SettingsPageViewController.cs`: the base class with the Mac members, a Wino zone over the backdrop, `StackedCard` for phone width, `Navigate`, `ISettingsPageTitleSource`.
  - `SettingsPageCatalog.cs`, `SettingsRootViewController.cs`, `SettingsSheetChrome.cs` (backdrop, bar appearance, Close button), `SettingsPageRoutes.cs`, `SettingsLifecycleHook.cs`, `SettingsRowParts.cs`.
  - `SettingsIcons.cs`: a local icon adapter; see Gaps.
  - `SettingsDebug.cs` (Debug only).
  - `Views/Dialogs/FormSheet.cs` (`FormSheet`, `FormSheetPopUp`).
  - `UIKitViewRegistration.Settings.cs`.
- **Pages:** under `Views/Settings/`, with the same class names as the Mac (list below), plus:
  - the `WinoAccount/` partials and `WinoAccountBenefitViews`;
  - the theme views (`ThemeColorWell`, `ThemeSurfacePreviewView`, `ThemeThumbnailView`);
  - `IntelligenceCoverageControls`, `WinoIntelligenceBrand`, `WinoIntelligenceControls` and `IntelligenceConsentPolicySheet`;
  - `MailFilterPresentation`, `MailFilterEditorPageViewController.Rows` and `SettingsDragList`.
- **Account sheets:** `Views/Account/`: `WinoAccountSheet` (base), `WinoAccountLoginSheet`, `WinoAccountRegistrationSheet`, `WinoAccountEmailConfirmationSheet`, `SyncSecretSheet`, `ServerCertificateTrustSheet`, `PurchaseChannelSheet`.
- **Dialogs:**
  - `Views/Dialogs/SignatureEditorSheet.cs` and `WinoRichTextEditorView.cs`. The editor is a WKWebView over `WebKitHtmlMailEditorSession`. Its assets are embedded resources of `Wino.Editor.Core`, so the head needs no bundle item.
  - `Infrastructure/UIKitDialogService.{AccountReorder,AliasCategory,ContactsTasks,Folders,KeyboardShortcut,Signature,WinoAccount}.cs`.

## Pages: route, host and presentation

**Hosts:**
- Every Settings route uses one host per idiom: `FullScreenSheet` on iPhone (J's host gives it a full-screen `UINavigationController`) and `Sheet` on iPad (form sheet).
- The first page presents the sheet; every later page pushes inside it through the shared `BreadcrumbNavigationRequested` → `NavigationCoordinator` → `ShowSettingsAsync`. Back pops it.
- The Close button on the first page of the stack calls `INavigationHost.CloseSettingsAsync`.

**Root list (`SettingsRootViewController`, `SettingOptionsPageViewModel`, routes `SettingsPage` and `SettingOptionsPage`):**
- A grouped table inside a Wino zone with a large title and settings search through the shared `SettingsNavigationInfoProvider.Search`.
- The Manage accounts row shows the account summary. The list reloads on `LanguageChanged` and when Intelligence access changes.

**Order:** groups, names and order come from the shared `SettingsNavigationInfoProvider`, the same source as the Windows pane and the Mac sidebar:
1. Untitled: Manage accounts, Wino Account, Wino Intelligence (only with the entitlement).
2. General: General, Notifications, Personalization, Keyboard shortcuts (iPad only), About.
3. Mail: Preferences, Message list, Unread badges, Read and compose, Email templates, Storage.
4. Calendar: Preferences, Rendering.
5. People: Preferences.
6. To Do: Preferences.

**Hidden on iOS:**
- by `SettingsPageCatalog.IsSupportedOnDevice`: Companion, Backup and restore, and Keyboard shortcuts on iPhone;
- by the shared `SettingsPageAvailability`: S/MIME (`IPlatformCapabilities.Smime` is false).

**Deviation from the brief's group list:** the brief listed "Notifications under Mail" and an "App" group. That list comes from the Mac design document; the code on Windows and Mac uses the shared provider order above, so I kept code parity.

**Root-level routes** (controller → `WinoPage`, all registered in DI):
- `AppPreferencesPageViewController` → AppPreferencesPage. Language only; there is no startup, close-behaviour or Sparkle card on iOS.
- `PersonalizationPageViewController` → PersonalizationPage. There is no window material.
- `ApplicationThemeGalleryPageViewController` → ApplicationThemeGalleryPage.
- `ApplicationThemeEditorPageViewController` → ApplicationThemeEditorPage.
- `NotificationSettingsPageViewController` → NotificationSettingsPage. It uses iOS alert sounds; "Snooze while presenting" is dropped.
- `KeyboardShortcutsPageViewController` → KeyboardShortcutsPage (iPad).
- `AboutPageViewController` → AboutPage.
- `StoragePageViewController` → StoragePage.
- `MailPreferencesPageViewController` → MailPreferencesPage.
- `MessageListPageViewController` → MessageListPage. There is no density or hover-action option.
- `ReadComposePanePageViewController` → ReadComposePanePage. There is no reading-pane layout or detached compose option.
- `UnreadBadgeSettingsPageViewController` → UnreadBadgeSettingsPage.
- `EmailTemplatesPageViewController` → EmailTemplatesPage.
- `CalendarPreferenceSettingsPageViewController` → CalendarPreferenceSettingsPage and CalendarSettingsPage.
- `CalendarRenderingSettingsPageViewController` → CalendarRenderingSettingsPage.
- `ContactsPreferenceSettingsPageViewController` → ContactsPreferenceSettingsPage.
- `ToDoPreferenceSettingsPageViewController` → ToDoPreferenceSettingsPage.
- `ManageAccountsPageViewController` → ManageAccountsPage and AccountManagementPage.
- `WinoAccountManagementPageViewController` → WinoAccountManagementPage.
- `WinoIntelligencePageViewController` → WinoIntelligencePage.

**Sub-pages, reached from their parent as on Windows:**
- `AccountUnreadBadgePageViewController` → AccountUnreadBadgePage, from Unread badges.
- `CreateEmailTemplatePageViewController` → CreateEmailTemplatePage, from Email templates.
- From Manage accounts and Account details:
  - `AccountDetailsPageViewController` → AccountDetailsPage;
  - `MergedAccountDetailsPageViewController` → MergedAccountDetailsPage;
  - `AliasManagementPageViewController` → AliasManagementPage;
  - `MailCategoryManagementPageViewController` → MailCategoryManagementPage;
  - `SignatureManagementPageViewController` → SignatureManagementPage;
  - `FolderCustomizationPageViewController` → FolderCustomizationPage;
  - `MailFiltersPageViewController` → MailFiltersPage;
  - `MailFilterEditorPageViewController` → MailFilterEditorPage;
  - `CalendarAccountSettingsPageViewController` → CalendarAccountSettingsPage.
- From the Wino Intelligence page:
  - `WinoIntelligenceManagementPageViewController` → WinoIntelligenceManagementPage;
  - `IntelligenceCoveragePageViewController` → IntelligenceCoveragePage.
- J's IMAP edit page (`ImapCalDavSettingsPage`, Sheet) pushes inside the Settings sheet. Add account opens J's onboarding secondary sheet.

## Dialog methods

**Now real** (every form is a `FormSheet` or `WinoAccountSheet`: a page sheet on iPhone and a form sheet on iPad, Cancel on the leading side, a consequence-led primary button on the trailing side, the Windows field order and an inline error):

| Method | Presentation |
| --- | --- |
| `ShowCreateAccountAliasDialogAsync` | Form: address, then reply-to. Returns a result whose alias is null when cancelled. |
| `ShowEditMailCategoryDialogAsync` | Form: name, then the shared palette (`WinoColorSwatchPicker`) |
| `HandleSystemFolderConfigurationDialogAsync` | Form: one pick-up row per special folder (as on the Mac) |
| `ShowNewContactListDialogAsync` | Form: account, then name |
| `ShowAccountReorderDialogAsync` | Sheet with a `UITableView` in editing mode (reorder handles), Save and Cancel |
| `ShowSignatureEditorDialog` | Large sheet: name, a formatting toolbar (B, I, U, bullets, numbering, link, clear) and the WKWebView editor. Same result contract as the Mac. |
| `ShowKeyboardShortcutDialogAsync` | Sheet that captures a hardware key through `PressesBegan`, with modifier toggles. Without a hardware keyboard it takes one letter or digit. Mac key names. |
| `ShowWinoAccountLoginDialogAsync` | Sheet: email, password, "Forgot password?". Uses the shared `WinoAccountAuthErrorTranslator`. |
| `ShowWinoAccountRegistrationDialogAsync` | Sheet, then the email confirmation sheet (`WinoAccountEmailConfirmationHelper`) |
| `ShowWinoAccountSyncSecretDialogAsync` | Sheet with secure fields (a confirm field for a new backup). It was an alert. |
| `ShowServerCertificateTrustDialogAsync` | Sheet with the certificate summary; fails closed. It was an alert. |
| `ShowUnlimitedAccountsPurchaseChannelDialogAsync` | Sheet: Apple App Store, or Stripe with a Wino Account (disabled with a sign-in hint when no Wino Account exists) |

**Unchanged from J:**
- The real prompts, pickers, file pickers, message source and account progress stay as they were.
- `ShowAccountProviderSelectionDialogAsync` still returns null and logs, as on the Mac: no shared caller uses it, and onboarding owns provider choice.

**Still unavailable:** none of the stubbed methods. One path is limited: keyboard shortcut recording on an iPhone without a hardware keyboard records only a single letter or digit, because there is no key-capture surface.

## New English strings needed

I added no strings. `resources.json` is untouched beyond the seed, and all 958 `Translator` keys used by M2 code exist. The keys below are needed because the reused Mac and Windows text names a Mac, the Dock or windows. Until they exist, the pages show the existing (Mac-worded) keys.

| Proposed key | English value | Replaces |
| --- | --- | --- |
| `IOS_UnlimitedAccountsPurchaseDialog_AppleAppStoreDescription` | The purchase is connected to the Apple Account signed in on this device. | `UnlimitedAccountsPurchaseDialog_AppleAppStoreDescription` ("…on this Mac") |
| `IOS_UnreadBadges_AppIconBadge_Title` | App icon badge | `UnreadBadges_DockBadge_Title` |
| `IOS_UnreadBadges_AppIconBadgeCaption` | app icon badge | `UnreadBadges_DockBadgeCaption` |
| `IOS_UnreadBadges_AppIconBadge_Description` | Add this account's unread total to Wino's combined app icon badge. | `UnreadBadges_DockBadge_Description` |
| `IOS_UnreadBadges_CountSource_Description` | Folder badges are about visibility. This is about importance. Only these folders feed the account and app icon totals. | `UnreadBadges_CountSource_DockDescription` |
| `IOS_UnreadBadges_Folders_Description` | Count feeds the account and app icon totals. Badge only shows a number in the folder list. | `UnreadBadges_Folders_DockDescription` |
| `IOS_Personalization_BackdropDescription` | Off keeps plain surfaces with the theme accent only. | `MacOS_Personalization_BackdropDescription` ("window surfaces") |

`AboutPageViewController` shows the product name "Wino Mail" as a literal, as the Mac does.

## Verification

All on the Mac (`~/Desktop/wt-M2`, the standard tar mirror). `WinoStoreKit2.xcframework` came from `wt-J` and `Sparkle.xcframework` from `~/Desktop/Wino-Mail`. Logs and screenshots are in `D:\Wino-Mail\artifacts\ios-platform\M2-*`.

**Builds:**

| Step | Result |
| --- | --- |
| Baseline head build before M2 | 0 errors (`M2-baseline-build.log`) |
| `dotnet build src/Wino.Mail.iOS/Wino.Mail.iOS.csproj -c Debug -r iossimulator-arm64 -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true`, final | **0 errors, 8 warnings.** All 8 are IL2104 trim notices, J's baseline (`M2-ios-debug-sim.log`). |
| `Wino.Mail.UIKit` with `-p:Nullable=enable --no-incremental` | 0 errors (`M2-uikit-nullable.log`). The M2 files carry only the known shared-contract warnings; see Gaps. There are no `!` operators or suppressions. |

**Simulators.** I used my own devices so the other agents' simulators were not disturbed: "M2 iPhone 18 Pro" `CD9D0936-1CB9-4E81-9860-6B5CD8617449` and "M2 iPad Pro 11" `717CC2B4-5B8A-4FA0-B475-A7FD79C5BEA8`, both iOS 27.0. The app starts on Welcome with no account. Settings opens through the debug bridge `settings` command (the shell's avatar is not reachable without an account), and pages open through `page X`.

| Device | Snapshots (`M2-<device>-<name>.png`) |
| --- | --- |
| iPhone | `root`, `general`, `personalization`, `messagelist`, `notifications`, `about`, `manageaccounts`, `winoaccount`, `mailpreferences`, `themegallery` |
| iPhone dialogs (one launch each, `M2-iphone-<dialog>-dialog.png`) | `alias`, `category`, `login`, `signature`, `purchase`, `reorder`, `certificate` |
| iPad | `root` (form sheet; Keyboard Shortcuts listed), `general`, `personalization`, `messagelist`, `notifications`, `about`, `manageaccounts`, `winoaccount`, `mailpreferences`, `themegallery`, `login-dialog` |

The app log and console showed no exceptions.

**Defects found in the runs and fixed:**
- **Settings sheet blank.** The backdrop was placed in front of the navigation stack (`WinoLayout.Fill` adds the subview on top); it is now sent behind it.
- **Form and Wino Account sheets empty.** The root scroll view had `TranslatesAutoresizingMaskIntoConstraints = false`, which gave it zero size.
- **Form fields too narrow.** The fields hugged their content; the rows now stretch.
- **Icons three times too large.** Tinted icons were oversized; `SettingsIcons` (see Gaps) fixes this.

**Debug bridge commands** (`SettingsDebug.cs`, Debug builds only):
- `settingspage WinoPage`;
- `dialog alias|category|signature|newlist|reorder|login|register|secret|certificate|purchase|shortcut|text` (opens without awaiting);
- `dismiss`;
- `iconsize`.

A programmatic `dismiss` does not complete a pending dialog, so the dialog gate stays held. Use one launch per dialog, as above.

## Gaps and pending manual checks

- **`WinoIcons.Image` scale defect (H's control, out of scope).**
  - **Defect:** with a tint, or in the colorful style, `WinoIcons.Image` returns a 1x image at three times its point size: the `UIImageAsset` is resolved without a display scale (`iconsize`: tinted `{60,60}@1` against template `{20,20}@3`).
  - **Local workaround:** M2 code goes through `SettingsIcons.Image`, which re-wraps the bitmap at its real scale. The light/dark variant is then fixed until the page is rebuilt.
  - **Still affected:** J's picker cells in `UIKitDialogService.cs` and other callers outside M2.
  - **Fix needed in `controls/Wino.Presentation.UIKit/WinoIcons.cs`:** resolve the asset with the display scale, or return the asset image itself.
- **Shared nullable contracts.** These are the same issue J reported, and show only under `-p:Nullable=enable`:
  - CS8604 on every page's `InitializeNavigationAsync(mode, parameter)`, because the shared parameter is non-nullable.
  - CS8613 on `ShowEditMailCategoryDialogAsync` and `ShowSignatureEditorDialog`, whose returns are nullable.
  - CS8766 on `ICreateAccountAliasDialog.CreatedAccountAlias`.
  - CS8604 on the `SystemFolderConfiguration` constructor (unset folders are null).
  - **Proposed Domain fix:** make those signatures nullable.
- **Unsaved-change confirmation.** It is not wired into the sheet's system back button (`IConfirmBackNavigation`, which the theme editor uses), so a swipe back can leave the theme editor without asking.
- **Checkout return.** The billing return parameter receiver (Mac `ISettingsPageParameterReceiver`) has no UIKit equivalent: a checkout return opens Wino Account as a new push.
- **Not exercised:** anything that needs an account or a network, including:
  - Account details and its sub-pages (aliases, categories, signatures with a real account, filters, folder customization, merged inbox, calendar account settings, account unread badge);
  - real Wino Account sign-in and registration;
  - App Store purchase;
  - theme editor wallpaper and colour pickers;
  - email template editing;
  - keyboard shortcut capture with a hardware keyboard;
  - Intelligence pages (no entitlement);
  - dark mode, Dynamic Type, rotation and VoiceOver;
  - theme backdrops other than Default.
- **Not run:**
  - the Windows build: no shared, Windows or Mac file changed;
  - Release iOS builds;
  - device builds.
