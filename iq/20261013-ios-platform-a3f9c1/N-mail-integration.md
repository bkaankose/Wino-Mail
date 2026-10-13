# N: Mail flow integration (K + L + M1 + J), both simulators

**Tree:** `D:\Wino-Mail`, branch `feature/iOS`, main working tree. Nothing is committed or pushed. New files are untracked; edits are unstaged. Deleted files were also removed from the index with `git rm --cached`: `IUIKitBriefingPageFactory.cs`, both `MailRowMapper.cs` copies and `PadMultiSelectionView.cs`.

**Mac mirror:** `~/Desktop/wt-N`. Helper scripts are in `~/Desktop/wt-N-tools`, with `run.sh` for launch, bridge commands and screenshots. The simulators are "N iPhone 18 Pro" (`E99E6623-5D2B-404C-9BD5-F57A5976FC20`) and "N iPad Pro 13" (`56CD0977-03B5-4022-AFBC-880E62B0007D`); both are shut down now.

**Result:**
- All builds and tests pass: Windows Debug and Release, ViewModels tests, the Mac head and the iOS head (details under Verification).
- The Mail flow runs on both idioms with the real reader, compose, Move, briefing, What's New, the Settings sheet and EML opening. iPad pop-out windows work.
- I found and fixed 3 runtime defects.

## What was wired

### 1. Registration (`src/Wino.Mail.UIKit/UIKitViewRegistration.cs`)
- `RegisterUIKitViews` calls `RegisterMailViews(services)` (M1).
- It also calls `RegisterSettingsViews(services)`. This is declared as `static partial void RegisterSettingsViews(IServiceCollection)` without a body. **M2 supplies only the implementing declaration.**
- It registers `Shell/SettingsRootPlaceholderViewController`.

The registry and registration classes are now `partial`, so O1 and O2 can add their files without editing these:
- `PhonePageRegistry` and `PadPageRegistry` (`sealed partial`)
- `PhoneViewRegistration` and `PadViewRegistration` (`static partial`)
- `UIKitViewRegistration`, which was already partial

### 2. Routes (one registration per idiom)

`PhonePageRegistry`:
- `MailListPage` → `ShellContent`.
- `this.RegisterMailPages(phone: true)`, which gives the reader its route and compose → `FullScreenSheet`.
- `RegisterHost(MailRenderingPage, Push)`, as K built it.
- `this.RegisterSettingsPages(phone: true)`.
- K's `IdlePage` host line and the Debug `ReaderStandInViewController` route are removed. On iPhone the idle page has no route, per M1.

`PadPageRegistry`:
- `MailListPage` → `ShellContent`.
- `RegisterMailPages(phone: false)`: reader, compose and idle all go to `RenderingFrame`.
- `RegisterSettingsPages(phone: false)`.
- L's commented lines are gone.

New `Shell/UIKitSettingsPageRoutes.RegisterSettingsPages`:
- It calls `static partial void RegisterSettingsPageRoutes(UIKitPageRegistry, bool phone)`, which M2 implements.
- If `SettingsPage` is still not registered after that, it registers the placeholder root as `FullScreenSheet` on **both** idioms (the avatar, Cmd+, and the `settings` bridge command).

Compose as an iPhone sheet:
- `UIKitNavigationService.ShowSettingsAsync` sets `ComposePageViewController.CloseHandler` to the new public `UIKitNavigationService.CloseSheetPageAsync(page)`.
- That method closes the sheet when the page is its root, or pops back to the page below it otherwise. It keeps the router's sheet bookkeeping.
- **Defect fixed:** a sheet dismissed outside the router (for example by the bridge) left a stale `_sheet`, so later Settings pages were pushed onto a dismissed stack. `ShowSettingsAsync` now drops a sheet that is no longer presented.

Briefing and What's New now use one set of presenters, M1's `IUIKitDailyBriefingPresenter` and `IUIKitWhatsNewPresenter`:
- **Deleted:** `Infrastructure/IUIKitBriefingPageFactory.cs` (K) and L's `IDailyBriefingPresenter` / `IWhatsNewPresenter` in `UIKitShellContracts.cs`.
- **iPhone:** `PhoneShellViewController.ShowBriefing()` calls `PresentSheet()`. The Mailboxes briefing button follows `IsAvailable` and updates on `UnseenItemsChanged`.
- **iPad:** the toolbar uses `CreatePanel(close)` in L's 400pt overlay (`BriefingPanelWidth = DailyBriefingPanelViewController.PanelWidth`), and the button is hidden unless `IsAvailable`. What's New uses `IUIKitWhatsNewPresenter`.
- L's debug command `briefing` was renamed to `padbriefing`, so it no longer overrides M1's `briefing [fake]`.

### 3. Reader commands and Move

- **iPhone toolbar:** `Phone/Mail/MailReaderToolbar.cs` was rewritten to bind `IMailReaderCommands`.
  - Archive/Unarchive, Delete, Move and Flag/Clear flag come from `PrimaryCommands`.
  - The Reply menu uses `CreateAction`. More is a deferred menu over `CreateMoreMenu`. The toolbar rebuilds on `CommandsChanged`.
  - It sets `ShowsCommandBar = false`.
  - `PhoneShellHost.Push` attaches the toolbar to any `IMailReaderCommands` controller. `MailListPageViewController.ReaderViewModel` was removed.
- **One Move abstraction:** `IMailMovePresenter` (from L's contracts), with one implementation per idiom.
  - The reader's Move command (`MailReaderCommand` `runFrom`) calls `TryShowMove([mail], anchor)`. If that fails, it falls back to the ViewModel command, which uses the shared folder picker.
  - iPad: L's `PadMailMovePresenter` (popover).
  - iPhone: new `Phone/Mail/PhoneMailMovePresenter.cs`. It calls `PhoneShellViewController.TryShowMove`, which calls `MailListPageViewController.TryShowMove(IReadOnlyList<MailCopy>)`. K's Move sheet was refactored from `MailItemViewModel` to `MailCopy`.
- **Defect fixed:** the shared `MailRenderingPageViewModel.MenuItems` contains no Move, because Windows moves from the list. The real reader's Move was therefore always disabled.
  - The UIKit reader now adds Move next to Delete when a message is loaded (`UpdateCommandBar`).
  - The Mac reader has the same gap (out of scope; noted below).
- **iPad reader column:** the column already showed the reader's own command bar (`ShowsCommandBar` defaults to true on iPad).

### 4. EML
`UIKitApplicationShellHost.OpenMailFileAsync` now calls `Views.Mail.EmlReaderSheet.OpenAsync(services, path, content)`.

### 5. Multi-scene (iPad pop-out)

**Info.plist:**
- `UIApplicationSupportsMultipleScenes` is now true.
- iPhone never opens a second scene.
- `SceneDelegate` also closes an extra main-window scene (for example, New Window from the iPad app switcher) with `RequestSceneSessionDestruction`, so the app keeps one main window.

**`SceneDelegate`:**
- It calls `PadShellHost.TryConnectPopOut` first.
- New `sceneDidDisconnect:` calls `PadShellHost.SceneDisconnected`.

**Compose:**
- `ComposePageViewController` implements `IReaderFrameChild`.
- It shows "Open in new window" (`Buttons_PopOut`, glyph `OpenInNewWindow`) when `FrameHost.CanOpenInNewWindow`.

**`PadShellHost.TryConnectPopOut`:**
- It sets the composer's `CloseHandler` to destroy its own window scene.
- **Defect fixed:** the page kept its reader-column Auto Layout pins, so the popped-out composer drew in a 200pt strip at the bottom over black. The view now uses autoresizing and the window has a system background.

**Integration defect fixed in `PadRenderingFrameHost`:**
- The column's navigation bar belongs to the frame host, so a docked composer's Send, More and Attach never appeared on iPad.
- The frame now shows the docked page's `RightBarButtonItems` on its leading side and clears them on detach.

### 6. Duplicates moved to `Wino.Mail.UIKit`
- **`Views/Mail/MailRowMapper.cs`** (public) replaces K's and L's copies.
  - The iPad keeps `Density(preferences)`. The iPhone uses `PhoneDensity` (Medium).
  - `Map` throws for a row that is not a mail item (iPad behaviour); `TryMap` returns null (iPhone behaviour).
- **`PadMultiSelectionView`** was deleted. The reader frame uses M1's `MailMultiSelectionView`, and the pad list maps `MailBulkAction` to `ShellCommand`.

### 7. WinoIcons sizing (`controls/Wino.Presentation.UIKit/WinoIcons.cs`)
- **Template path:** already correct. K's `PhoneBarItems.Glyph` drew exactly what it draws.
- **Tinted or colorful path (`MonoImage`, `Image` with a tint):** this is where the bug was. The `UIImageAsset` variants were registered without a display scale, and once UIKit copied them (as a template or tinted copy) they came back at scale 1, which is the 3× oversize.
- **Fix:** both variants are rendered with one explicit renderer format and registered and resolved with that format's scale (`GetTraitCollectionByModifyingTraits(DisplayScale)`, the non-obsolete iOS 17 API).
- **New `WinoIcons.TemplateImage(glyph, size)`:** the mono template whatever the icon style, for bars, tabs and menus.
- **Workaround removed:** `PhoneBarItems.Glyph` is gone, and all phone call sites, including J's tab images, call `WinoIcons.TemplateImage`.
- **Not done:** I added no scale probe to the bridge, so the asset path is not checked numerically. The screenshots below show correctly sized glyphs in the tab bar, the reader toolbar, the iPad toolbar, the reader command bar and the swipe/menu icons.

### 8. Shared nullable signatures

**Navigation parameters:** `object parameters` became `object? parameters` in `INavigationAware.OnNavigatedTo/From` and in every override, `InitializeNavigationAsync`, `InitializeAsync(NavigationMode, object)`, `MailListPageViewModel.DeactivateAsync` and `MailRenderingPageViewModel.LoadContentAsync`.
- A `-p:Nullable=enable` build of `Wino.Mail.ViewModels` showed only `LoadContentAsync` dereferencing `parameters`. It pattern-matches, so the change is safe (`N-vm-nullable.log`).

**`AliasManagementPageViewModel.SetSelectedSigningCertificate(MailAccountAlias, X509Certificate2?)`.**

**`IMailDialogService`:**
- Nullable returns: `ShowMoveMailFolderDialogAsync` → `IMailItemFolder?`, `ShowAccountPickerDialogAsync` → `MailAccount?`, `PickFolderAsync` → `IMailItemFolder?`, `ShowSignatureEditorDialog` → `AccountSignature?`, `ShowEditMailCategoryDialogAsync` → `MailCategoryDialogResult?`.
- Nullable parameters: `ShowEditMailCategoryDialogAsync(MailCategory? category = null)` and `ShowKeyboardShortcutDialogAsync(KeyboardShortcut? existingShortcut = null)`.
- Both `#pragma warning disable CS8625` blocks were removed.
- WinUI `DialogService` and the Mac `AppKitDialogService*` were updated to match. `UIKitDialogService` (M2's file) was not touched; the iOS build reports no warnings for it.

**`!` removed in the Mac head:**
- `null!` passed to `OnNavigatedTo`/`OnNavigatedFrom`/`DeactivateAsync` in 7 files.
- `certificate!` in `AliasManagementPageViewController`.
- `existing!` in `DialogsDebug`.
- `return picked!` and `return null!` in `AppKitDialogService`, `.AliasCategory` and `.Signature`.

**Sentinels removed:** K's and L's `NoParameter` placeholders are gone; both list pages now pass null.

**Files:**
- Domain: `Interfaces/INavigationAware.cs`, `Interfaces/IMailDialogService.cs`.
- WinUI: `Services/DialogService.cs`.
- Mac: `Infrastructure/AppKitDialogService.cs`, `.AliasCategory.cs`, `.Signature.cs`, `Infrastructure/WinoViewController.cs`, `Views/Calendar/CalendarPageViewController.cs`, `Views/Contacts/ContactsPageViewController.cs`, `Views/Mail/ComposePageViewController.cs`, `MailListPageViewController.cs`, `MailRenderingPageViewController.cs`, `Views/Settings/AliasManagementPageViewController.cs`, `DialogsDebug.cs`, `SignatureAndEncryptionPageViewController.cs`.
- `Wino.Mail.ViewModels`, 49 files:
  - Root: `AccountDetails`, `AccountManagement`, `AccountSetupProgress`, `AccountUnreadBadge`, `AliasManagement`, `AppPreferences`, `BackupRestore`, `Compose`, `ContactEdit`, `Contacts`, `ContactsPreferenceSettings`, `FolderCustomization`, `Idle`, `ImapCalDavSettings`, `IntelligenceCoverage`, `MailAppShell`, `MailCategoryManagement`, `MailList`, `MailPreferences`, `MailRendering`, `MergedAccountDetails`, `MessageList`, `ProviderSelection`, `SignatureAndEncryption`, `SignatureManagement`, `SpecialImapCredentials`, `Storage`, `Test`, `ToDo`, `ToDoPreferenceSettings`, `UnreadBadgeSettings`, `WinoIntelligenceManagement`.
  - `Calendar/*`: `AccountSettings`, `AppShell`, `EventCompose`, `Page`, `EventDetails`.
  - `Common/*`: `AccountManagementPageViewModelBase`, `ApplicationThemeEditor`, `ApplicationThemeGallery`, `CoreBaseViewModel`, `KeyboardShortcuts`, `NotificationSettings`, `Personalization`, `SettingOptions`, `WhatsNew`, `WinoAccountManagement`.
  - `Shell/WinoAppShellViewModel`.

The legacy `Wino.Core.ViewModels` and `Wino.Calendar.ViewModels` projects (in no solution) were not changed.

### 9. Strings
- No keys were added. All 481 `Translator.*` references in the iOS projects already exist in `en_US/resources.json`, including M1's `Composer_HideKeyboard` and `Composer_CloseDraftMessage`.
- The proposed `IOS_KeyboardShortcuts_SoftwareKeyboardHint`, Unread badges "App icon" and `NotificationSound_None` keys are not referenced by any code in the tree yet. M2 adds them with its pages.

### Debug aids
- **K's `Mail/Debug/` folder never reached the main tree,** because `.gitignore` ignores `[Dd]ebug/`. I restored it from `~/Desktop/wt-K`, split as follows:
  - `Wino.Mail.UIKit/Views/Mail/MailDebugSeed.cs` (shared `seedmail`, both idioms).
  - `Phone/Mail/Diagnostics/PhoneMailDebugHook.cs`: `mailboxes`, `openfolder`, `toolbarinfo`, `pop`, plus new `readertoolbar`, `readertap N` and `newmail`.
- **New bridge commands:**
  - `eml-activate [PATH]`: the activation path through `OpenMailFileAsync`.
  - `reader-tap OPERATION`: runs a reader primary command anchored to its bar.
  - `compose-popout`.
  - All are `#if DEBUG`.

## Verification

Logs and screenshots are in `artifacts/ios-platform/`.

| Check | Result |
| --- | --- |
| Windows Release gate (WinUI x64, `GenerateAppxPackageOnBuild=false`) | **0 errors, 0 warnings** (`N-windows-release-x64.log`) |
| Windows Debug x64 WinUI | **0 errors, 0 warnings** (`N-windows-debug-x64.log`) |
| `dotnet test tests/Wino.Mail.ViewModels.Tests -c Debug -p:Platform=x64` | **186/186 passed** (`N-viewmodels-tests.log`) |
| Mac head Debug osx-arm64 | **0 errors, 54 warnings**, J's baseline (`N-mac-debug-arm64.log`) |
| iOS head Debug iossimulator-arm64 | **0 errors, 8 warnings**, all IL2104 trim notices (`N-ios-debug-sim.log`). The one earlier CS9124 and CA1422 in my code were fixed. |

**iPhone screenshots (`N-phone-*`):**

| Screenshot | Shows |
| --- | --- |
| `shell` | Inbox list with tab bar glyphs at the correct size |
| `02-mailboxes` | Mailboxes |
| `03-reader` | Real reader with K's toolbar built from `IMailReaderCommands` |
| `04-reader-move` | Toolbar Move → `PhoneMailMovePresenter` → Move sheet |
| `05-compose-sheet` | Routed `ComposePage` as a full-screen sheet with Cancel |
| `06-settings` | Settings full-screen sheet with the placeholder root, after a compose sheet was dismissed outside the router |
| `07-briefing` | Briefing sheet |
| `08-whatsnew` | What's New |
| `09-eml` | `eml-activate` → `OpenMailFileAsync` → EML sheet |

The `05-compose-sheet` evidence is `compose-dump: sheetRoot=True`.

**iPad screenshots (`N-pad-*`):**

| Screenshot | Shows |
| --- | --- |
| `01-shell-idle` | Split view with the idle page routed into the reader column |
| `02-reader` | Reader in the column with its own command bar |
| `03-reader-move` | Command bar Move → popover anchored to the bar |
| `04-compose-frame` | Compose in the reader column with Send, More, Attach and Open in new window in the column bar |
| `05-briefing-overlay` | L's 400pt overlay over M1's panel |
| `06-whatsnew` | What's New |
| `07-settings` | Settings full-screen sheet |
| `08-eml` | EML sheet |
| `09-popout` | Composer moved to its own window scene; the frame becomes empty (`padinfo reader=`) |

The consoles show no managed exceptions after the fixes.

**Runtime defects found and fixed:**
1. Startup crash: an `InvalidCastException` from `UIGraphicsImageRendererFormat.PreferredFormat`, which the binding types as the base class. The fix uses `DefaultFormat`.
2. The routed reader's Move was always disabled.
3. A stale router sheet after an outside dismissal.

Also fixed: the pop-out composer layout, and the docked composer's bar items not appearing on iPad.

## Remaining gaps and manual checks

**Gaps:**
- **iPhone New Message** (Mailboxes and list compose button, bridge `newmail`) did not open a composer for the seeded IMAP demo account, and nothing was logged; `mailop Reply` behaved the same. The compose route itself works (shown with `page ComposePage`). Check with a real account whether draft creation reaches `SelectMailItemContainerEvent` → `ComposePage` on iPhone, or whether the demo account (failing sync) blocks draft preparation.
- **iPad search field:** with the composer docked, the reader column's centred search field collapses to a compact search button, because the composer's 4 items share the bar. The layout should be decided.
- **Mac reader:** it also has no Move once a message loads, because the shared reader menu has none. Out of scope here; the same UIKit approach would fix it.
- **Not merged:** O1 and O2 partials. M2 needs to implement `RegisterSettingsViews` and `RegisterSettingsPageRoutes` only (no new declarations), register `SettingsPage` as `FullScreenSheet` on both idioms, and add the proposed string keys with its pages.
- **Pop-out reader:** not built. Only the composer offers Open in new window. A popped-out composer's Send and Cancel close its scene through `CloseHandler`, which was not exercised.

**Manual checks pending:**
- Taps on the Reply and More menus, swipe and long-press menus.
- Compose Cancel/Send from the sheet on a real draft.
- Choosing a folder in Move.
- The phone briefing button (hidden without Wino Intelligence access).
- iPad Split View/Stage Manager collapse and rotation.
- Closing a pop-out window from the app switcher (draft save).
- Dark mode, Dynamic Type, VoiceOver.
- A real IMAP, Outlook or Gmail account.
