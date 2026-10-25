# macOS shared-logic roadmap

This roadmap orders the work that moves business rules duplicated in the Mac app into shared code, so both applications run the same rules.
It is a plan, not an implementation record. Each phase is its own task with its own approval, verification, and handoff.

Evidence: the duplication inventory of 7 October 2026 (`docs/macos-shared-logic-alignment.md`, a local working document; entry numbers below refer to it). Entries were rechecked against source at commit `61b7ea3a` on 12 October 2026. Recheck an entry again when its phase starts: the Mac app changes quickly.
Paths: **M** = `src/Wino.Mail.MacOS`, **CA** = `controls/Wino.Mail.Controls.AppKit`.

## Rules for every phase

- **Shared ownership.** A rule moves into the existing shared library that already owns its neighbours: `Wino.Core.Domain` (enums, glyph maps, formatting contracts), `Wino.Services` (persistence, preferences), `Wino.Mail.ViewModels` (command availability, menu and operation models, navigation results), or `controls/Wino.Mail.Controls.Core` (layout calculations, row metrics). Do not create a new project or a generic UI framework.
- **Platform adapters stay thin.** WinUI and AppKit render the shared model. They keep presentation only: controls, menus, sheets, popovers, key names, and native windows.
- **Windows code-behind is evidence, not policy.** When Windows keeps a rule in code-behind or `XamlHelpers`, compare both copies and choose the correct behavior before moving it. Fix the Windows copy too when it is wrong.
- **Intentional AppKit differences stay.** The decisions in [macOS design decisions](macos-design-decisions.md) (separate Settings window, sheets and popovers, menu bar, Mac key names, native window hosting) are not duplication.
- **Delete last.** Remove a Mac or Windows copy only after both applications consume the shared implementation and the phase's checks pass on both platforms.

## Status after recheck

| Status | Entries |
| --- | --- |
| Resolved | #1 shortcuts read `IKeyboardShortcutService` (`AppDelegate.Shortcuts.cs`); #51 the dead `MacThemeService` is gone |
| Partly resolved | #2 hover actions use the shared command and the on/off preference, but always show four fixed buttons and ignore the left/center/right action preferences; #5 the briefing presenter follows entitlement messages; the Mail-only rule is unverified; #12 account creation runs `SynchronizeCreatedAccountAsync`; mode selection still differs; #36 `SettingsMenuProvider` is registered, but the Settings sidebar still mirrors its rules in `SettingsPageCatalog`; #37 the Settings presenter consults `IConfirmBackNavigation`; history still recreates pages; #41 account details and the reorder dialog use `MailAccountIconInfoFactory`; other account visuals are unverified |
| Open, confirmed | #3 and #6 have no Mac recipient; #7 the shortcut dialog offers every action outside Mail and Calendar; #8 `MailOperationPresentation`; #11 own readable-text formula in CA; #14 activation passes only `NavigationMode.New`; #18 static `PendingSelection`; #19 no `ChangeMenuSelectionAsync`; #25 copied registration lists (`MacViewModelRegistration*.cs`); #26 context menu built in the view; #30 special-folder glyph map in two Mac files and `XamlHelpers`; #31 own list-date format; #32 no `IsSecondaryMenuPreferred` or Save as PDF/EML; #34 recipient check accepts any address with `@`; #38 the Intelligence page initializes its ViewModel more than once; #43 approximated recurrence text; #44 calendar layout maths in CA; #50 copied theme catalog; #52 copied provider glyph map; #55 copied account error text; #58 copied briefing card rules; #59 copied state persistence |
| Not rechecked | #4, #9, #10, #13, #15–17, #20–24, #27–29, #33, #35, #39, #40, #42, #45–49, #53, #54, #56, #57 |

## Status after the iOS Phase 0 (13 October 2026)

The iOS platform work moved shared prerequisites out of the Mac head before adding a third app. These entries changed. Manual runtime checks on both platforms are still pending, so Phase 4 deletion has not started for any of them.

| Entry | What landed | Remaining |
| --- | --- | --- |
| #25 copied registration lists | `RegisterWinoViewModels()` in `Wino.Mail.ViewModels` holds every shared ViewModel with the WinUI lifetimes. WinUI, the Mac and the iOS head call it; `MacViewModelRegistration*.cs` keeps only views. | None |
| #12, #37 account lifecycle and back confirmation | `NavigationCoordinator` (`Wino.Mail.ViewModels/Navigation`) owns history, `IConfirmBackNavigation`, account created and removed, and `IsModeAvailable`. `AppKitNavigationService` and `UIKitNavigationService` are host adapters. | WinUI `NavigationService` does not use it; mode selection after account creation and the Settings window history are unchanged |
| Notification responses, activation, start and stop order (not numbered) | `NotificationResponseService` (`Wino.Services`), `ActivationRouter` and `ApplicationLifecycleCoordinator` (`Wino.Mail.ViewModels`). The Mac handlers are thin adapters; the notification delegate is `AppleNotificationResponseHandler` in `Wino.Platform.Apple`. | WinUI toast and activation handlers do not use them yet |
| #50 theme catalog | `WinoThemeCatalog` (`Wino.Core.Domain`) feeds `MacWinoThemeService` and WinUI `NewThemeService` | None |
| #8 operation presentation | `MailOperationDisplay` and `FolderOperationDisplay` (`Wino.Core.Domain`). The Mac uses both; WinUI delegates folder operations. | WinUI mail operation glyphs and titles differ from the shared map (Move, Focused/Other, AlwaysMove, RetryDraftUpload); pick one set |
| #30 special-folder glyphs and menu rules | `SpecialFolderGlyphs` (`Wino.Core.Domain`) and `ShellMenuItemRules` (`Wino.Mail.ViewModels`). The Mac uses both; WinUI delegates the folder glyph. | WinUI shell templates still hold the menu rules |
| #52 provider glyphs, #55 account error text | `MailProviderGlyphs`; `WinoAccountAuthErrorTranslator` and `WinoAccountEmailConfirmationHelper` moved from WinUI into Domain. `WinoAccountErrorText.cs` is deleted. | None |
| #36 Settings page visibility | `SettingsPageAvailability` (`Wino.Core.Domain`), used by `SettingsPageCatalog.IsVisible` on the Mac and WinUI `SettingsMenuProvider` | The rest of the Mac `SettingsPageCatalog` was not rechecked |
| #31 list date, #43 recurrence text | `MailRowPresentation` (`Wino.Mail.ViewModels`) formats list and reader dates through `DateTimeDisplayFormatter`; `CalendarEventDisplay` (`Wino.Core.Domain`) gives the full recurrence summary. The Mac uses both. | WinUI `CalendarXamlHelpers` (no all-day branch, Ical.Net recurrence text) |
| #44 calendar layout | `CalendarSurfaceLayoutCalculator` (`Wino.Core.Domain`). The AppKit and UIKit calculators are thin adapters. | WinUI `TimedCalendarLayout` and `MonthCalendarLayout`; the swap is not mechanical |
| #11 readable text colour | `ReadableTextColor` (`Wino.Core.Domain`) keeps both rules under separate names; the Mac and WinUI use it | `Wino.Core/Misc/ColorHelpers.GetReadableTextColorHex` duplicates it |
| Row metrics, avatar palette, mini-calendar grid, intelligence header state | Moved to `controls/Wino.Mail.Controls.Core`; typed bindings and tokens to `controls/Wino.Presentation.Core` | WinUI `ContactVisual` uses its own hash and initials |

## Phase 1: Restore shared behavior

Make the Mac app run rules that already exist in shared code, or that it currently ignores.

- **Entries:** missing message handling (#3, #6); ignored preferences (#2, #21, #31, #32); entitlement and availability checks (#5, #7, #36); validation (#34); formatting (#11, #31, #43).
- **Shared ownership:** existing services and ViewModels for preferences, entitlement, and shortcuts. `EmailValidator` checks move from the Windows view into the compose ViewModel. List dates use `DateTimeDisplayFormatter` and recurrence text uses `CalendarRecurrenceSummaryFormatter` (both in `Wino.Core.Domain`); extend them rather than adding new formatters. The readable-text rule uses `ColorHelpers`.
- **Platform adapters:** Mac message recipients in the shell and navigation service; hover buttons built from `LeftHoverAction`, `CenterHoverAction`, and `RightHoverAction`; the shortcut dialog takes its action list from the shared service.
- **Prerequisites:** none. This phase is the first.
- **Native acceptance:** a sync failure shows an info bar; New Mail with several accounts asks for the account; hover-action preferences change the row buttons; Contacts and To Do offer only their supported shortcuts; invalid recipients are rejected the same way on both platforms.

## Phase 2: Align navigation and lifecycle

Give both hosts the same navigation contract, so page-level workarounds can go.

- **Entries:** #4, #12–#19, #23, #24, #37, #38, #25.
- **Shared ownership:** route kinds, mode-scoped back stacks, re-entry rules, navigation results, and unsaved-change handling in the shared navigation contracts and `WinoAppShellViewModel`. ViewModel registrations and lifetimes move into one shared registration extension that both composition roots call.
- **Platform adapters:** `AppKitNavigationService` and `SettingsWindowPresenter` implement the contract. Native window hosting, the separate Settings window, and sheets stay Mac-specific.
- **Prerequisites:** Phase 1 message handling (#3, #6), because shell recipients move with the host.
- **Native acceptance:** Back restores the previous page in each mode; the unsaved-changes prompt appears once; a new account opens in the mode Windows selects; the What's New button hides after opening from About; Settings Back works without recreating pages.

## Phase 3: Extract reusable rules

Move rules that Windows keeps in WinUI helpers, code-behind, or flyout builders into shared libraries, then point both apps at them.

- **Entries:** operation and menu models (#8, #10, #26–#29, #45, #48, #49); theme catalog (#50, #39); account and folder glyph mappings (#30, #52, #42); layout calculations and row metrics (#35, #44); dialog and presentation rules (#40, #47, #53–#58); persistence (#59).
- **Shared ownership:** operation presentation, context-menu item models, move-target predicates, and reply/flag/read availability in `Wino.Mail.ViewModels`; glyph maps and the theme catalog in `Wino.Core.Domain`; timed and month calendar layout and row density metrics in `controls/Wino.Mail.Controls.Core`; state persistence in `Wino.Services`.
- **Platform adapters:** WinUI flyout builders and AppKit `NSMenu` builders render the shared item models. Each platform keeps its own icons, key-equivalent text, and colors.
- **Prerequisites:** Phase 2 for menu items that navigate (#19, #26).
- **Native acceptance:** context menus, folder icons, provider icons, themes, and calendar layouts match Windows in content and order on both platforms. Pixel parity is not required.

## Phase 4: Remove superseded copies

Delete each Mac copy, and the WinUI helper it was ported from, once both applications use the shared implementation.

- **Entries:** every copy replaced in Phases 1–3, including `MailOperationPresentation`, `MacWinoThemeService`'s catalog, `MacStatePersistenceService`, the copied glyph maps, `WinoAccountErrorText`, the static `PendingSelection`, and the duplicated shared-layer types in `Wino.Mail.ViewModels/Common`.
- **Prerequisites:** the phase that replaced the copy has passed its checks on both platforms.
- **Build check:** both solutions build with no remaining references to the deleted types.
- **Native acceptance:** a short regression pass on both platforms for the features whose copies were removed.

## Verification for each phase

- Windows: Debug x64 build, the Release x64 gate, and manual lab checks, as [AGENTS.md](../AGENTS.md) requires for shared changes.
- macOS: native Debug arm64 and x64 builds and the native acceptance checks above. See the [macOS development guide](harness/development-macos.md).
- Report a phase complete only when both platforms have evidence. A build does not prove behavior.
