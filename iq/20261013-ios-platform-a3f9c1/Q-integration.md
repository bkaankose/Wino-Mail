# Q: Integration of M2 (Settings) and O2 (Contacts, To Do), open Mail fixes, verification on both simulators

**Tree:** `D:\Wino-Mail`, branch `feature/iOS`, main working tree. Nothing is committed or pushed. Deleted files were removed from the index with `git rm --cached`. Someone else staged some of my edits during the run; the index and the worktree both hold the changes.

**Mac mirror:** `~/Desktop/wt-Q` (standard tar mirror, plus `WinoStoreKit2.xcframework` from `wt-J` and `Sparkle.xcframework` from `~/Desktop/Wino-Mail`). Tools: `~/Desktop/wt-Q-tools` (`build.sh`, `run.sh` with `launch|cmd|shot`). Simulators: "Q iPhone 18 Pro" `D76B0C84-E70C-4B1B-AF3D-FADD445C3A9E` and "Q iPad Pro 13" `8CFEFB3D-65B7-4FBA-9C11-B03FEB6D8385` (iOS 27.0). Both are still booted.

**Result:**
- All builds pass with 0 errors.
- The walk ran on both idioms: Welcome → seeded demo account → Mailboxes/sidebar → list → reader → Move → New Message → Reply → Settings (root, General, Message list, Notifications, Manage accounts, Create category dialog) → Contacts → To Do → briefing → What's New.
- iPhone New Message and Reply now open the composer.
- On iPad, the docked composer no longer collapses the search field.
- The router closes a single-page sheet on back, so O2's anchor workaround is gone.

## Fixed

### 1. Settings route mechanism (one mechanism kept)
- **Defect:** N registered the placeholder as `SettingsPage` from the registry constructors. M2's `SettingsLifecycleHook` adds routes only when `SettingsPage` is unmapped, so the merged tree would never have shown M2's pages.
- **Kept:** registration from the registry constructors, which is deterministic and runs before any navigation.
  - `SettingsPageRoutes.RegisterSettingsPages(this UIKitPageRegistry)` (M2's file) registers every Settings route as `FullScreenSheet` on both idioms, following the iOS design decision.
  - `PhonePageRegistry` and `PadPageRegistry` call `this.RegisterSettingsPages()`.
- `SettingsLifecycleHook` keeps only its catalog and Close setup. Its route registration and `SettingsPageRoutes.DefaultHost` are removed.
- **Deleted:** `Shell/UIKitSettingsPageRoutes.cs` (N's partial hook) and `Shell/SettingsRootPlaceholderViewController.cs`, along with its DI registration and the orphaned doc comment in `UIKitViewRegistration.cs`.
- **Change from M2:** M2 used a form sheet (`Sheet`) on iPad. It is now full screen there too.

### 2. `SettingsIcons` workaround removed
- `Views/Settings/SettingsIcons.cs` is deleted, and its 28 call sites in 15 files now call `WinoIcons.Image` directly.
- **Verified:** the bridge `iconsize` reports `tinted {20,20} @3 template {20,20} @3`, and the screenshots show correctly sized glyphs (`Q-phone-10-settings-root`, `Q-phone-14-settings-manageaccounts`).

### 3. Strings
- Added M2's seven keys to `en_US/resources.json` with the values from the M2 report:
  - `IOS_UnlimitedAccountsPurchaseDialog_AppleAppStoreDescription`
  - `IOS_UnreadBadges_AppIconBadge_Title`
  - `IOS_UnreadBadges_AppIconBadgeCaption`
  - `IOS_UnreadBadges_AppIconBadge_Description`
  - `IOS_UnreadBadges_CountSource_Description`
  - `IOS_UnreadBadges_Folders_Description`
  - `IOS_Personalization_BackdropDescription`
- The pages use them: `PurchaseChannelSheet`, `AccountUnreadBadgePageViewController` (6 references) and `PersonalizationPageViewController`.
- O2's four keys (`IOS_ContactAction_Call`, `IOS_ContactAction_FaceTime`, `IOS_ToDoListsTitle`, `IOS_ContactEditor_Protocol`) are present. No other locale was touched.

### 4. iPhone New Message and Reply (N's open item a)
- **Diagnosis:** the new Debug bridge command `composeprobe` in `PhoneMailDebugHook` runs `HandleCreateNewMailAsync` and reports its inputs. It showed:
  `selected=FolderMenuItem … drafts=Drafts … error=Primary alias does not exist for this account. Creating draft failed.`
- **Cause:** K's seeded demo account has no root alias. Drafts folder, route and handlers were all fine. The exception was only routed to `Logger.CaptureException`, so nothing appeared on screen.
- **Fix (debug seed only):**
  - `MailDebugSeed` creates the root alias (`CreateRootAliasAsync`), and repairs an already-seeded account.
  - O2's `ContactsDebugCommands` demo account gets one too.
- **Result:** New Message (`Q-phone-04-newmail`) and Reply from the reader (`Q-phone-08-reply`, `To: Ada…`, subject `Re: …`) open the full-screen composer sheet. On iPad they open the docked composer (`Q-pad-05-newmail`, `Q-pad-06-reply`).
- **Still pending:** the same flow with a real account. It gets its alias from onboarding.

### 5. iPad docked composer search field (N's open item b)
- `PadRenderingFrameHost` no longer puts the page's bar items into the column's navigation bar.
- A command row (a transparent `UIToolbar`) at the top of the reader zone now holds Send, More, Attach and Open in new window, leading-aligned like the Mac composer command row. It collapses when the page has no items.
- The shell toolbar keeps its full-width search field (`Q-pad-05-newmail`).
- Pop-out still works (`Q-pad-07-popout`; `padinfo reader=` is empty afterwards).

### 6. Navigation: back inside a single-page sheet (task 4)
- **Change:** `UIKitNavigationService.TrySettingsGoBack` (UIKit host only; `NavigationCoordinator` is unchanged, so Mac behaviour is unaffected):
  - with a tracked sheet, back pops a sheet that has two or more pages and closes a single-page sheet;
  - a sheet dismissed outside the router is dropped, and back falls through to the history;
  - off the main thread, the decision runs on the main queue.
- No shared code changed, so no ViewModels test was added. The existing 186 tests pass.
- **Removed:** O2's `Views/Contacts/SheetRouteAnchorViewController.cs` and its hook in `ContactEditPageViewController`. Registry comments were updated.
- **Verified on both idioms:**
  - contact editor Cancel closes the sheet back to the card (`Q-phone-23`, `Q-pad-23`);
  - Save of an edited contact closes it (`Q-phone-24-contact-after-save`);
  - router back on the composer closes the composer sheet (`Q-phone-05-compose-back`);
  - back on the Settings root closes Settings and keeps the shell content (`Q-phone-15-settings-closed`, `Q-pad-15-settings-closed`).

### 7. Other defects found in the walk
- **iPhone full-screen composer sheet was black** around the clear-rooted composer zone. `UIKitShellHostBase.PresentSheet` now gives full-screen sheets `SystemGroupedBackground`.
- **`WhatsNewPresenter` stayed stuck** after its sheet was dismissed outside the presenter: `_page` stayed set, so What's New never opened again. It now releases a sheet that is no longer presented, then presents a new one (`Q-phone-41b-whatsnew-again`).
- **iOS Release error CS0649** on `Pad MailListPageViewController._densityOverride`, which only the Debug `density` command assigns. The field is now Debug-only; Release uses a null property. No gate was changed.

### O2 workarounds kept
- The iPhone To Do Lists root (`PhoneToDoPageViewController` + `ToDoListsViewController`). This needs a shell-owned per-mode menu root, which is not a one-line hook.
- The iPad `ShowInReader` usage.

## Shell hooks added
- `SettingsPageRoutes.RegisterSettingsPages()`, called by both registries.
- Close-on-back for single-page sheets in `UIKitNavigationService`.
- The reader-column command row in `PadRenderingFrameHost`.
- The full-screen sheet background in `UIKitShellHostBase`.

The registry and registration files are only extended; nothing under Calendar was touched.

## Verification

Logs and screenshots are in `D:\Wino-Mail\artifacts\ios-platform\Q-*`. Mac logs are in `~/Desktop/wt-Q/artifacts/ios-platform/Q-*.log`.

| Check | Result |
| --- | --- |
| Windows Release gate (WinUI x64, `GenerateAppxPackageOnBuild=false`) | 0 errors, 0 warnings (`Q-windows-release-x64.log`) |
| `dotnet test tests/Wino.Mail.ViewModels.Tests -c Debug -p:Platform=x64` | 186/186 passed (`Q-viewmodels-tests.log`) |
| iOS head Debug `iossimulator-arm64` | 0 errors, 8 warnings (all IL2104). A clean first build also lists CS8601/CS8613 nullable warnings in `Wino.Platform.Apple` (F's code, not UIKit). |
| iOS head Release `iossimulator-arm64` | First run: 1 error, CS0649 (fixed, see item 7). Final run: 0 errors, 8 warnings (all IL2104). The `Wino.Platform.Apple` nullable warnings stay warnings; that project does not treat warnings as errors. |
| Mac head Debug osx-arm64 (ad hoc signing flags) | 0 errors, 54 warnings (baseline) |
| `WinoMail.iOS.slnx` Debug | 0 errors, 25 warnings (`Q-ios-slnx-debug.log`): IL2104 trim notices plus the `Wino.Platform.Apple` CS8601/CS8613 nullable warnings (F's code). None are in UIKit code. |

**Screenshots:**

| | iPhone | iPad |
| --- | --- | --- |
| Welcome | `Q-phone-01-welcome` | `Q-pad-01-welcome` |
| List and shell | `02-list` | `02-shell` |
| Mailboxes | `03-mailboxes`, `15-settings-closed` | |
| Reader | `06-reader` | `03-reader` |
| Move | `07-reader-move` | `04-reader-move` |
| New Message | `04-newmail` | `05-newmail` |
| Reply | `08-reply` | `06-reply` |
| Pop-out | | `07-popout` |
| Settings | `10-settings-root`, `11-settings-general`, `12-settings-messagelist`, `13-settings-notifications`, `14-settings-manageaccounts` | same names |
| Dialog | `16-dialog-category` | `16-dialog-category` |
| Contacts | `20-contacts`, `21-contact-card`, `22-contact-editor`, `23-contact-editor-cancelled`, `24-contact-after-save` | `20-contacts-card`, `22-contact-editor`, `23-contact-editor-cancelled` |
| To Do | `30-todo`, `32-todo-detail` | `30-todo-detail` |
| Briefing | `40-briefing` (fake data) | `40-briefing` |
| What's New | `41-whatsnew`, `41b-whatsnew-again` | `41-whatsnew` |

No crashes and no managed exceptions in the consoles. The app log shows only the expected sync failures of the demo account's closed loopback port.

## Remaining gaps
- **Draft-creation failures are silent.** Errors other than `MimePersistenceException` thrown by `HandleCreateNewMailAsync` reach only `Logger.CaptureException` from the shell's `SelectMenuItem`; the user sees nothing. This is shared behaviour; an InfoBar would make it visible.
- **Debug bridge race.** `contacts-edit` immediately followed by `contacts-edit-save` in one batch left Save running and blocked the bridge (bridge `sleep` does not delay). With separate commands, Save works. This looks like a Save issued before the editor initialised (the destination picker path), not a product defect.
- **iPhone To Do:** after `seedtasks`, My Day shows no back control to Lists in the screenshot (`Q-phone-30-todo`). Check by hand that Lists is reachable.
- **Mac-worded copy still shown on iOS:**
  - the General page description mentions startup and close behaviour;
  - What's New notes are Windows-worded ("system tray, Ctrl+Shift+Space");
  - the Manage accounts upgrade card text crowds its title line on iPhone (`Q-phone-14`).
- **Settings on iPad is now full screen** instead of M2's form sheet. Confirm against the design.
- **Code-rule violations:** L's Pad list still has `null!` field initialisers. The `Wino.Platform.Apple` nullable warnings (F) are unaddressed. Both are outside this assignment.
- **Real-account checks:** New Message and Reply with a real IMAP, Outlook or Gmail account.

## Consolidated manual checks (N + M2 + O2 + Q)

**Mail:**
- Reply and More menus, swipe and long-press menus.
- Compose Cancel and Send on a real draft.
- Choosing a folder in Move.
- The phone briefing button (needs Intelligence access).
- The iPad command row with real taps, including Open in new window and closing a pop-out from the app switcher (draft save).
- iPad Split View, Stage Manager, rotation and compact width.

**Settings:**
- Account details and its sub-pages (aliases, categories, signatures, filters, folder customization, merged inbox, calendar account settings, account unread badge).
- Wino Account sign-in and registration.
- App Store purchase.
- Theme editor pickers and the unsaved-change prompt on swipe back.
- Email templates.
- Hardware-keyboard shortcut capture.
- Intelligence pages.
- Theme backdrops other than Default.
- Checkout return.

**Contacts:**
- Swipe, long-press and filter menus.
- Typed search, pull to refresh.
- Call and FaceTime (device).
- Photo, list and category toggles.
- A saved edit persisting.

**To Do:**
- Quick add with the keyboard.
- Step and title edits.
- Date picker.
- iPhone Lists navigation.
- iPad bulk selection.

**All:** dark mode, Dynamic Type, VoiceOver, hardware keyboard, and real Outlook, Gmail, IMAP or CardDAV data.

**Builds:** device-signed builds (no App ID or profile yet).
