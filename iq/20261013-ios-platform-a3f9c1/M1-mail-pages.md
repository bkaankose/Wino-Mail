# M1: idiom-neutral Mail pages and shell extras (UIKit)

**Worktree:** `D:\Wino-Mail\.claude\worktrees\agent-a226da3d0fc4f4fb1` (branch `worktree-agent-a226da3d0fc4f4fb1`, HEAD 80ca7f38), seeded with `seed-after-J.patch`. The seed is staged by `git apply --3way`; M1 work is unstaged or untracked. Nothing is committed.
**Mac mirror:** `~/Desktop/wt-M1`, with its own simulators "M1 iPhone 18 Pro" `B938FE20-DBAF-44B7-9483-CE73695D23AC` and "M1 iPad Pro 11" `CDC3355B-08D3-4C56-8ED4-15580B527047`, so other agents' installs are unaffected.
**Compose:** a sub-agent ported it in the same worktree. Its results are merged into this report.

## Files

Everything is under `src/Wino.Mail.UIKit/`.

**Edited files**
- `UIKitViewRegistration.cs`: only `static class` → `static partial class`.
- `src/Wino.Core.Domain/Translations/en_US/resources.json`: 2 keys added at the end, `Composer_HideKeyboard` and `Composer_CloseDraftMessage`. Other locales are untouched.

**New: registration**
- `UIKitViewRegistration.Mail.cs`: `static partial void RegisterMailViews(IServiceCollection)`. It registers:
  - transient `MailRenderingPageViewController`, `ComposePageViewController`, `IdlePageViewController`, `WhatsNewPageViewController`;
  - singleton `DailyBriefingPresenter`/`IUIKitDailyBriefingPresenter` and `WhatsNewPresenter`/`IUIKitWhatsNewPresenter`;
  - `ShellExtrasLifecycleHook`, which creates both presenters at ServicesReady;
  - Debug only: the `MailDebugCommands` lifecycle hook.

**New: `Views/Mail/` (reader)**
- `MailRenderingPageViewController.cs` with `.Recipients`, `.Intelligence` and `.Debug` partials.
- `ReadingPaneViews.cs`, all public so K and L can use them:
  - `MailIdleView`, `MailMultiSelectionView` + `MailBulkAction`, `MailLoadingView`, `MailSkeletonView`;
  - `MailReaderCommand`, `IMailReaderCommands`, `MailReaderCommandBar`;
  - `MailReaderStatusRow`, `MailLinkButton`.
- `IdlePageViewController.cs`.
- `EmlReaderSheet.cs`: the Mac `EmlReaderWindow` as a sheet.
- `UIKitMailPageRoutes.cs`.
- `MailDebugCommands.cs` (Debug only).

**New: `Views/Mail/` (compose, sub-agent)**
- `ComposePageViewController.cs` with `.Recipients`, `.Attachments`, `.Toolbar`, `.Options`, `.Extras`, `.Rewrite`, `.Find`, `.Drop` and `.Debug` partials.
- `Compose/`: `ComposeEditorWebView` (overrides `InputAccessoryView`), `EditorFormatToolbar`, `EditorToolbarItem`, `EditorDialogs`, `ComposeRecipientField`, `ComposeRecipientSuggestionList`, `ComposeFindBar`, `ComposeTemplatePicker`, `ComposeIntelligenceRing`.

**New: `Views/Extras/`**
- `DailyBriefingPanelViewController.cs`, `DailyBriefingPresenter.cs`.
- `WhatsNewPageViewController.cs`, which also holds `WinoVersionTab` and `WinoFeatureRow`.
- `WhatsNewPresenter.cs`, `ExtrasContracts.cs`, `ShellExtrasLifecycleHook.cs`.

## Integration actions (outside M1 scope, not done)

1. **Call the registration.** `RegisterUIKitViews` must call `RegisterMailViews(services);`. This is one line, and M2's partial needs the same. The brief allowed only the `partial` keyword in the existing file. Without that line the Mail controllers, presenters and debug commands are not registered. Verification patched the line in the Mac mirror only.
2. **Add the routes.** `PhonePageRegistry` (K) and `PadPageRegistry` (L) call `this.RegisterMailPages(phone: true|false)` from their constructors.
3. **Open .eml files.** `UIKitApplicationShellHost.OpenMailFileAsync` should return `Views.Mail.EmlReaderSheet.OpenAsync(services, path, content)`. It currently returns false.

## Routes (`UIKitMailPageRoutes.RegisterMailPages`)

| Page | iPad (`phone: false`) | iPhone (`phone: true`) |
| --- | --- | --- |
| `MailRenderingPage` | `RenderingFrame` (reader column) | `RenderingFrame` (the phone host pushes it) |
| `ComposePage` | `RenderingFrame` | `FullScreenSheet`, using the same controller |
| `IdlePage` | `RenderingFrame` | not routed; an iPhone never shows an idle reader |

- What's New and the Daily briefing are not `WinoPage` routes; their presenters show them.
- `MailIdleView` and `MailMultiSelectionView` are reader-column states for L. `MailMultiSelectionView` raises `ActionInvoked(sender: button, MailBulkAction)` and `UnselectAllInvoked`.

## `IMailReaderCommands` (implemented by `MailRenderingPageViewController`)

```csharp
public interface IMailReaderCommands
{
    IReadOnlyList<MailReaderCommand?> PrimaryCommands { get; } // Windows order; null = divider
    UIMenu CreateMoreMenu(UIView? anchor);   // overflow operations + Reader View + reader theme toggle
    event EventHandler? CommandsChanged;     // raised after every VM MenuItems change
    bool ShowsCommandBar { get; set; }       // default: false on iPhone, true on iPad
}
public sealed class MailReaderCommand : ICommand  // CanExecute == IsEnabled
{
    MailOperation Operation; WinoIconGlyph Glyph; string Title; bool IsEnabled;
    void Invoke(UIView? anchor); UIAction CreateAction(UIView? anchor);
}
```

- **Primary order:** Reply, Reply all, Forward | Archive/UnArchive, Delete (soft or hard), Move, Flag/ClearFlag, Mark read/unread.
- **Before a message loads:** the default set, disabled.
- **.eml:** only Save As and Print.
- **For K:** cast the pushed controller to `IMailReaderCommands`. Build the bottom toolbar from `PrimaryCommands`, picking by `Operation`; Reply, Reply all and Forward can go into one Reply `UIMenu` through `CreateAction`. Use `CreateMoreMenu` for More, and rebuild on `CommandsChanged`.
- **For L:** the iPad command bar is inside the page; nothing to wire.

**Other public surfaces**
- **Reader:** `MailRenderingPageViewController.RenavigateAsync(object?)` reuses the reader for the next message, as on the Mac. Close sends `DisposeRenderingFrameRequested`, except in the EML sheet, which dismisses itself.
- **Compose:**
  - `ComposePageViewController.IsSheetRoot` is true at the root of a presented navigation controller. The page then shows a Cancel button with Save / Discard / Cancel through the dialog service.
  - `Func<Task>? CloseHandler` runs after Send or Cancel. When it is null, a sheet root dismisses itself through `IUIKitShellHost.DismissSheetAsync`, which bypasses `UIKitNavigationService`'s sheet bookkeeping; otherwise the page sends `DisposeRenderingFrameRequested`. **K:** when compose is routed as a sheet, set `CloseHandler`, or check the navigation service's `_sheet` state.
- **Briefing (`IUIKitDailyBriefingPresenter`):**
  - `CreatePanel(Action close)` returns one cached `DailyBriefingPanelViewController`. **L:** add it as a child overlay, `PanelWidth = 400`.
  - `PresentSheet()` presents it with medium and large detents. **K:** use this on iPhone.
  - Also `HasUnseenItems`, `IsAvailable` and `UnseenItemsChanged` for the badge.
  - The panel loads on appear or unhide, and marks the briefing viewed on disappear or hide.
- **What's New (`IUIKitWhatsNewPresenter.ShowAsync()`):** opens a sheet with Done. It also answers `WhatsNewOpenRequested`, marks the version seen and sends `WhatsNewOpened`. The illustrations are read from `<app>/Assets/WhatsNew`.

## Design notes

**Reader**
- **One vertical scroll:** the header, the attachment strip and the body card scroll together. The `WKWebView` is as tall as its document (KVO on `contentSize`), and its own scroll view keeps horizontal panning.
- **Remote content:** follows the CSP policy from `MailRenderingOptions.LoadImages`.
- **Links:** go through `IExternalLauncher` via `WebKitHtmlMailReaderSession.Configure`.
- **Attachments:** tap → `OpenAttachmentCommand` (the platform launcher / QuickLook). The context menu offers Open and Save. Save all → `SaveAllAttachmentsCommand`, which uses M2's dialog-service folder picker.
- **Print and PDF:** `UIKitMailPrintPresenter`. After release, its web-view getter returns null, so the result is Unavailable.
- **Contact card:** a popover, kept as a popover on iPhone.
- **Glyphs:** WinoIcons everywhere; no SF Symbols.

**Idle**
- Shows `WinoLogo.png` from the bundle, falling back to the Mail glyph.
- The hint line is `MacOS_Reader_KeyboardHint`.

**Daily briefing panel:** the privacy line is `DailyBriefing_LocalHint`.

## Verification

Logs and screenshots are in `<worktree>/artifacts/ios-platform/M1-*`, copied to `D:\Wino-Mail\artifacts\ios-platform\`.

| Check | Result |
| --- | --- |
| `Wino.Mail.iOS` Debug `-r iossimulator-arm64 -p:WinoTargetPlatform=iOS` (mirror, plus the one-line registration patch) | **0 errors.** The 8 warnings are the J baseline IL2104 notices. (`M1-ios-debug-sim.log`) |
| `Wino.Mail.UIKit` Release (warnings as errors) | 0 errors, 0 warnings in M1 files. An IL2026 from `RegisterAll` was fixed with per-message `Register`. (`M1-uikit-release.log`) |
| `Wino.Mail.UIKit` Debug `-p:Nullable=enable` | 0 warnings in the reader and extras files. 6 remain in `ComposePageViewController.cs` (CS8604/CS8625), all from the non-null annotations on `ComposePageViewModel`. Details under Gaps. (`M1-uikit-nullable.log`) |
| iPhone simulator, debug bridge (`M1-iphone-out.log`) | Every step passed: idle (default and mail-empty), What's New (1 release, 13 features, 1120×600 image), briefing sheet with fake cards (4 cards, 7 dates), EML reader (recipients, date, 2 attachments, body 817pt, contact card), PDF export (16 KB), compose full-screen sheet (Cc/Bcc reveal, body focus, Insert toolbar section). No managed exceptions in the console. |
| iPad simulator (`M1-ipad-out.log`) | The same sequence passed, with the briefing as a 400pt overlay and compose as a form sheet. |

**Screenshots** (`M1-iphone-*` and `M1-ipad-*`):

| Screenshot | Content |
| --- | --- |
| `01-idle`, `02-idle-empty` | Idle page |
| `03-whatsnew` | What's New |
| `04-briefing`, `05-briefing-fake` | Daily briefing |
| `06-reader` | Reader |
| `06b-contact-card` | Contact card |
| `07-compose` | Compose |
| `08-compose-keyboard`, `09-compose-toolbar-insert` | Compose with body focus |

**Issues found and fixed during the runs**
- The reader bar labels were truncated on iPhone because they were measured before layout; the bar now measures with `SizeThatFits` and does a second stack pass.
- `MailLinkButton` used the `UIButton(UIButtonType)` constructor, which UIKit does not allow in subclasses. It now uses the default constructor.
- The debug `close` command now dismisses the top-most presented controller.

**Run note:** two iPad runs stalled partway, both while the Mac's load average was 29–48 because other agents were building. Re-runs on the same build passed. These look like host stalls, not page faults.

## Not exercised, or pending manual checks

**Needs an account**
- The reader on a real message: download, loading skeleton, intelligence header, unsubscribe and S/MIME rows, category chips, command-bar actions, Reply → compose.
- The reader inside the iPad reader column and as an iPhone push: needs K and L's shells, routes, and the registration line.
- Compose with a draft: From/aliases, token chips, suggestions from the shared recipient history, Send, templates, signatures, rewrite, attachments, Cancel → Save / Discard.

**Not testable from snapshots**
- The formatting bar as `inputAccessoryView`. The Simulator used the hardware keyboard and the snapshot does not include the keyboard window, so it needs a manual check with the software keyboard.
- The iPhone compose bar showed Attach and two More-style buttons; Send seems to have been moved into the bar's overflow. Check the filled Send on a phone width.

**Not tested**
- The print panel (`reader-print` exists), QuickLook attachment open, UIDocumentPicker Save all, and opening links.
- What's New in a regular-width container (list beside the card). In an iPad form sheet the size class is compact, so the stacked layout showed.
- Dark mode, Dynamic Type, rotation and VoiceOver.

**Gaps compared with the Mac**
- **Reader pop-out:** not ported; it waits for iPad multi-scene. `ComposeRequested` is not observed.
- **Briefing presenter:** the iPhone sheet replaces the panel's close action with "dismiss sheet". The last `CreatePanel` caller wins, as on the Mac.
- **Compose (from the sub-agent):**
  - No emoji insert.
  - No "More Colors…".
  - No arrow-key selection in the suggestion list.
  - Drop accepts files only; there is no separate images zone.
  - The 11pt chip text in `WinoChipView` may look small.
  - The header does not collapse while typing on small iPhones.
- **Shared contract:** `ComposePageViewModel` delegates and `InitializeNavigationAsync(object)` are non-nullable but checked for null. The proposed fix is to make them nullable in `Wino.Mail.ViewModels`, the same issue J reported. The reader avoids this with no-op delegates and an explicit null-parameter guard.

**Other platforms:** the Windows Release gate and the Mac head builds were not run. M1 changed no shared code apart from the two English strings.
