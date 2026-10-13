# H: UIKit presentation and controls (T13, Phase 2)

Worktree: `D:\Wino-Mail\.claude\worktrees\agent-a1e33b947024242f4` (branch `worktree-agent-a1e33b947024242f4`), seeded with `artifacts/ios-platform/patches/phase0-main.patch`. Nothing is committed: `git apply --3way` staged the Phase 0 seed, and my edits are unstaged on top of it.

- Mac mirror: `~/Desktop/wt-H`. Scratch tools (the iOS API dump and the verify script) are in `~/Desktop/wt-H-tools`.
- The bindings project in this worktree is still `Wino.Core.MacOS.Bindings`.
- In the mirror, the StoreKit2 xcframework came from `~/Desktop/wt-F` and the Sparkle xcframework from `~/Desktop/Wino-Mail`.

## Projects

`controls/Wino.Presentation.UIKit/Wino.Presentation.UIKit.csproj`
```xml
<TargetFramework>net10.0-ios</TargetFramework>
<SupportedOSPlatformVersion>17.0</SupportedOSPlatformVersion>
<RootNamespace>Wino.Presentation.UIKit</RootNamespace>
<PropertyGroup Condition="'$(Configuration)' == 'Release'"><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
<ProjectReference Include="../../src/Wino.Core.Domain/Wino.Core.Domain.csproj" />
<ProjectReference Include="../Wino.Presentation.Core/Wino.Presentation.Core.csproj" />
<PackageReference Include="CommunityToolkit.Mvvm" />
```

`controls/Wino.Mail.Controls.UIKit/Wino.Mail.Controls.UIKit.csproj` has the same shape. It references Domain, `Wino.Presentation.UIKit` and `Wino.Mail.Controls.Core`.

`controls/Wino.Editor.WebKit/Wino.Editor.WebKit.csproj`
```xml
<TargetFrameworks>net10.0-macos;net10.0-ios</TargetFrameworks>
<TargetFrameworks Condition="$(RuntimeIdentifier.StartsWith('osx-'))">net10.0-macos</TargetFrameworks>
<TargetFrameworks Condition="$(RuntimeIdentifier.StartsWith('ios-')) or $(RuntimeIdentifier.StartsWith('iossimulator-'))">net10.0-ios</TargetFrameworks>
SupportedOSPlatformVersion: 14.0 for -macos, 17.0 for -ios
TreatWarningsAsErrors: Release, -ios TFM only (the macOS TFM keeps the head's policy)
ProjectReference: Wino.Core.Domain, Wino.Editor.Core   (Wino.Presentation.AppKit removed; nothing used it)
```

- **RID condition.** It is the same rule F used for `Wino.Platform.Apple`. Without it, the Mac head (`-r osx-arm64`) fails restore with NU1102, because the -ios TFM asks for the osx-arm64 Mono runtime.
- **TFM version.** Unversioned `net10.0-ios` resolves to TargetPlatformVersion 26.0 for these libraries. The Release `-getProperty` values are SupportedOSPlatformVersion 17.0, TreatWarningsAsErrors true and IsAotCompatible false. The Phase 3 head must use the same TFM spelling. If the app resolves to 27.0 (G's finding), pin `net10.0-ios26.0` everywhere instead.
- **Nullable.** `Directory.Build.targets` sets `<Nullable>annotations</Nullable>` repo-wide, so nullable warnings never surface in normal builds.
  - I built the UIKit graph once with `-p:Nullable=enable`. It reported one warning in my code (`WinoIcons.RegisterFont`, now fixed) and none in the other new files.
  - The new code has no null-forgiving operators and no suppressions.

## Control inventory (UIKit twin, AppKit source, deviations)

### Presentation.UIKit (`Wino.Presentation.UIKit`)
- **`Layout`, `WinoLayout`** (twin of `Layout.cs`)
  - Same names; `Stack` takes `UILayoutConstraintAxis`.
  - Added `Insets` (twin of NSStackView.EdgeInsets) and `Priorities`.
- **`WinoIcons`, `WinoIconView`** (twin of `WinoIcons.cs`)
  - CoreText registers the fonts from `<bundle>/Fonts/`.
  - Glyphs come through the shared `WinoIconGlyph`.
  - `Image` renders through `UIGraphicsImageRenderer`. In the mono style with no tint it returns a template image; otherwise it returns a light and dark `UIImageAsset`.
  - Added `MonoImage` (replaces the AppKit `WinoToDoStyle.MonoImage`).
  - `WinoIconView` redraws through the iOS 17 `RegisterForTraitChanges` and on `WinoIcons.StyleChanged`.
- **`WinoStyle`, `WinoAccessibility`, `WinoTraits`**
  - Tokens forward to `WinoTokens`.
  - Colours are UIColor semantic colours or come from `FromDynamicProvider`.
  - Fonts use iOS sizes scaled by `UIFontMetrics` (Body 17, Caption 12, Description 13, Heading 20, PageTitle 28). `Scaled` and `Fixed` helpers were added.
  - `ZoneRadius` is 10, per the iOS design decision.
  - No `Symbol` or `SymbolView`, because there are no SF Symbols.
  - `Help` maps to `AccessibilityHint`.
- **`WinoThemeSurfaces`**: same API over UIColor pairs.
- **`WinoSurfaceView`, `WinoSeparator`, `WinoZoneView`**
  - The separator is a hairline sized by display scale.
  - The zone has radius 10, a 1pt stroke and a shadow drawn with a shadow path.
- **`ThemeBackdropView`** (the Mac twin lives in the head)
  - Passive `SetBackdrop(UIImage?, uint[]? gradient)`.
  - Draws a top-anchored aspect fill, or a `CAGradientLayer` from (0,0) to (0.32,1).
- **`WinoBarBackdropView`** (twin of `WinoPaneBackdropView`): a chrome blur plus the theme veil.
- **Not ported:** `WinoCheckbox`.

### Mail.Controls.UIKit (`Wino.Mail.Controls.UIKit.<Folder>`)

**MailList**
- `WinoMailRowCell` is a UITableViewCell that merges the AppKit row view and row container:
  - the highlight box and the 3pt pill;
  - the select-mode tint;
  - a round check that replaces the checkbox.
- `WinoMailRowModel` and `WinoMailRowTile` use UIColor and UIImage with the same members.
- `WinoMailRowMeasure` measures line heights that follow Dynamic Type.
- `WinoMailGroupHeaderView` is a UITableViewHeaderFooterView. `WinoMailListTheme` was also ported.
- `WinoChipView` and `WinoFlowView`.
- `WinoPivotBar` uses UISegmentedControl and adds `FillsWidth`.
- `WinoSearchScopeBar`: the scope pull-down is a UIButton menu.
- Hover actions work with the iPad pointer, but `HoverActionsEnabled` is off by default. Swipe actions belong to the page.

**Shell**
- `WinoShellRowCell` is new; on AppKit only the row background exists.
  - `ConfigureNewItem`: a 50pt accent row.
  - `ConfigureAccount`: a 50pt row with a 28pt `WinoAccountIconView`, the unread pill and `SetSync`.
  - `ConfigureFolder`: glyph, pill, depth and disclosure, 44pt.
  - `ConfigureHeader`.
  - Selection draws the fill and a 3pt pipe. `ShowsIndicator`, `Highlightable` and `IsDropTarget` are supported.
- `WinoUnreadPill` and `WinoSyncBar`.
- `WinoModeSwitcher` is a card.
  - **By default it shows the four app modes.** `includeSettings: true` gives the five desktop segments. The brief asked for five segments, but the settled iOS design decision says Settings is never a mode.
  - The selected segment shows its filled glyph in the accent colour.
- `WinoMiniCalendarView` uses the shared grid, taps and swipes, and one UIAccessibilityElement per day.

**Common**
- `WinoAccountIconView` and `WinoContactPicture`.
- `WinoInfoBar` uses WinoIcons severity glyphs (Info, CheckmarkCircleFilled, Warning, DismissCircle). The AppKit bar used SF Symbols.

**Settings**
- `WinoSettingsCard`, `Expander`, `Group`, `WinoLabeledSwitch` (UISwitch), `WinoColorSwatchPicker`, `WinoThemeTile` and `WinoSettingsStyle`.
- `SettingsBinder` keeps the AppKit method names:
  - `Checkbox` returns a check-style UIButton.
  - The two `PopUp` methods return pull-down menu buttons.
  - `Stepper` is a UITextField plus a UIStepper.
  - `TimePicker` is a compact UIDatePicker.
  - `OnActivated(UIControl, Action, UIControlEvent = PrimaryActionTriggered)`.
  - `SettingsDisposable`.

**Calendar**
- `WinoCalendarSurfaceView`, `WinoCalendarItemView`, `CalendarTileModel` (UIColor fill), a `CalendarLayoutCalculator` adapter over the Domain `CalendarSurfaceLayoutCalculator`, `WinoCalendarDragGhostView` and the event args.
- Touch mapping:

  | Gesture | Result |
  | --- | --- |
  | Tap | Item click or slot click |
  | Double tap | `ItemDoubleClicked` |
  | Long press on a tile | Lifts the tile to move it; a bottom-edge long press resizes it |
  | Release without moving | `ItemRightClicked` |
  | Long press on empty space | Drags out a range |

- `CalendarItemClickedEventArgs.Recognizer` replaces `NativeEvent`.
- `PreviewDrag` was not ported.

**Extras**
- `WinoPressableView` handles touch tracking, pointer hover and VoiceOver activate.
- `WinoStateView`, `WinoBriefingCardView` and `WinoDatePager`. The date pager stays in Extras, like on AppKit.

**ToDo**
- `WinoTaskRowCell` (twin of `WinoTaskCardView`) and `WinoTaskGroupHeaderView`.
- `WinoRoundCheckbox`.
- `WinoChipButton`: `Menu` is a `UIMenu` on an overlay button.
- `WinoQuickAddView` and `WinoToDoStyle`.

**Contacts**
- `WinoContactRowCell`, `WinoContactGroupHeaderView`, `WinoContactFieldCard`, `WinoCategoryChip` and `WinoContactStyle`.

**Intelligence**
- `WinoIntelligenceHeaderView`. The language pickers are UIButton menus, and Copy uses UIPasteboard.

**Not ported (not in the brief):** `WinoCollectionView`, the settings sidebar cell, the drawer rows and the What's New views.

UIKit name clashes needed `new`:
- `ReuseIdentifier` constants on the cells;
- the object `Tag` on chips and pressables;
- the settings card's `Description`;
- `Add` on the expander, the group and the state view.

## Logic moved into shared code (used by both AppKit and UIKit)

### `Wino.Mail.Controls.Core` (pure math)
- `Layout/WinoLayoutMath.cs`
  - `WinoLayoutFrame`.
  - `WinoFlowLayout`, from the flow view.
  - `WinoSwatchGridLayout`.
  - `WinoAspectFill`.
- `WinoControlText.cs`
  - `UnreadPill`.
  - `WithCount`.
  - `WinoListDiff.ReusablePrefix`, used by the mail row tiles and the contact chips.
  - `WinoPagerState`.
- `Settings/SettingsValueRules.cs`: index selection, stepper clamp and parse, and time-picker minutes.
- `Calendar/WinoMiniCalendarGrid.cs`: header button bounds, header hit test and title width.
- `Calendar/WinoCalendarSurfaceGeometry.cs`
  - The surface constants.
  - Hit tests and `Snap`.
  - Selection segment bounds.
  - All-day, timed and month drag resolution.
  - The initial scroll offset and the weekend rule.
- `IntelligenceHeader/WinoIntelligenceHeaderViewState.cs`: the header's expansion and panel state machine, and its visibility and enabled rules.

### `Wino.Presentation.Core/Controls/WinoControlRules.cs`
These rules use Domain types, and Controls.Core has no Domain reference. `Wino.Presentation.Core.csproj` now also references `Wino.Mail.Controls.Core`; WinUI does not reference Presentation.Core. The file holds:
- `WinoModeSegments` (`All`, `AppModes`).
- `WinoAccountIconGlyphs.ProviderGlyph`.
- `WinoCalendarTileAppearance`.
- `WinoIntelligenceHeaderText` (every Translator lookup of the header).
- `WinoContactRowRules`.

### AppKit files updated to call the shared code
All are in `controls/Wino.Mail.Controls.AppKit`, and behaviour is unchanged:
- MailList: `WinoChipView`, `WinoPivotBar`, `WinoSearchScopeBar`, `WinoMailRowView`. `ReadableText` now uses the Domain `ReadableTextColor`, which is the same formula.
- Shell: `WinoUnreadPill`, `WinoModeSwitcher`, `WinoMiniCalendarView`.
- Common and Contacts: `WinoAccountIconView`, `WinoContactRowView`.
- Extras: `WinoDatePager`.
- Settings: `SettingsBinder`, `WinoColorSwatchPicker`, `WinoThemeTile`.
- Calendar: `WinoCalendarItemView`, `WinoCalendarSurfaceView` and its Drag and Selection partials.
- Intelligence: `WinoIntelligenceHeaderView` and its Panels partial.

One intended change: the Mac `ShowSummary()` now refreshes the header even when no summary is available.

### Tests
`tests/Wino.Mail.Controls.Tests/SharedControlRulesTests.cs` adds 28 cases, so the count goes from 58 to 86.

## Editor rename

The rename was done with `git mv`, from `controls/Wino.Editor.AppKit` to `controls/Wino.Editor.WebKit`.
- Session types: `WebKitBrowserSession`, `WebKitHtmlMailEditorSession`, `WebKitHtmlMailReaderSession`.
- Namespace: `Wino.Editor.WebKit`.
- `using AppKit;` was removed.
- `MacOsMailDocumentBuilder` became `AppleMailDocumentBuilder` (also a `git mv`; same output).
- The only AppKit-typed code was in `FocusEditorAsync`. It is now `#if IOS BecomeFirstResponder() #else Window?.MakeFirstResponder(...)`.

Changes in `src/Wino.Mail.MacOS` are using and reference lines only:
- `Wino.Mail.MacOS.csproj`: the project reference path.
- `Views/Dialogs/WinoRichTextEditorView.cs`, `Views/Mail/Compose/EditorFormatToolbar.cs` and `Views/Mail/ComposePageViewController.cs`: `using Wino.Editor.WebKit;` plus `using AppKitHtmlMailEditorSession = Wino.Editor.WebKit.WebKitHtmlMailEditorSession;`.
- `Views/Mail/MailRenderingPageViewController.cs`: `using Wino.Editor.WebKit;` plus `using AppKitHtmlMailReaderSession = Wino.Editor.WebKit.WebKitHtmlMailReaderSession;`.
- `Views/Mail/ComposePageViewController.Find.cs`: `using Wino.Editor.WebKit;`.

The aliases mean no type references had to change. A later head edit can drop the aliases.

### Solutions and docs
- `WinoMail.MacOS.slnx`: the editor project path is renamed.
- New `WinoMail.iOS.slnx`:
  - `/iOS/`: the two UIKit projects.
  - `/Apple/`: `Wino.Editor.WebKit`.
  - `/Shared/`: Domain, Core, Core.ML, Services, Authentication, Messaging, ViewModels, SourceGenerators, Presentation.Core, Mail.Controls.Core, Editor.Core.
  - `/Tests/`: the four test projects.
  - Later assignments add the iOS head, `Wino.Mail.UIKit*`, `Wino.Platform.Apple`, `Wino.Platform.iOS` and `Wino.Core.Apple.Bindings`.
- `controls/AGENTS.md` and `controls/README.md` now name the WebKit and UIKit projects.
- `docs/appkit-platform-migration-plan.md` still says Editor.AppKit. It is a historical plan and was left untouched.

## Verification
Logs are in `D:\Wino-Mail\artifacts\ios-platform\H-*.log`.

| Command | Result |
| --- | --- |
| Windows `dotnet test tests/Wino.Mail.Controls.Tests -c Debug -p:Platform=x64` | 86/86 passed |
| Windows `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` | 0 errors, 0 warnings |
| Windows `dotnet build controls/Wino.Presentation.Core -c Release -p:Platform=x64` (AOT gate) | 0 errors, 0 warnings |
| Mac: Wino.Presentation.UIKit, Debug, iOS | 0 errors, 0 warnings |
| Mac: Wino.Mail.Controls.UIKit, Debug | 0 errors, 0 warnings |
| Mac: Wino.Mail.Controls.UIKit, Release (warnings as errors) | 0 errors, 0 warnings |
| Mac: Wino.Editor.WebKit, Debug, both TFMs | 0 errors, 0 warnings |
| Mac: Mac head, Debug, osx-arm64, ad-hoc signing | 0 errors, 45 warnings. All 45 are existing warnings in files this work did not change. |
| Mac: `WinoMail.iOS.slnx`, Debug, iOS | 0 errors. The first full run showed 3 existing xUnit2031 warnings in Wino.Core.Tests. |
| Mac `dotnet test tests/Wino.Mail.Controls.Tests` | 86/86 passed |

Not run:
- The Mac x64 build and the Mac Release builds.
- iOS runtime and visual checks, because there is no head yet.

Manual Mac checks are pending for the refactored AppKit controls:
- mini-calendar header;
- calendar tap, drag, resize and range selection;
- intelligence header panels and language menus;
- settings pop-ups, steppers and time pickers;
- swatch picker and theme tiles;
- mail row tiles;
- contact chips;
- date pager;
- mode switcher.

## What Phase 3 must supply
- **Fonts.** Bundle resources `Fonts/WinoIcons.ttf`, `Fonts/WinoIconsColor-Light.ttf` and `Fonts/WinoIconsColor-Dark.ttf` (as on the Mac head), and call `WinoIcons.Register()` at launch.
- **Theme images.** Theme wallpapers loaded into `ThemeBackdropView.SetBackdrop`.
- **Theme service.** A UIKit theme service that sets:
  - `WinoStyle.AccentOverride` and `HasBackdrop`;
  - `WinoThemeSurfaces.Apply`;
  - `WinoIcons.Style`;
  - the bar materials.
- **WKWebView.** The head creates and configures the WKWebView and owns the keyboard accessory and the print formatter. It hands the view to the WebKit sessions.
- **Page code.** Pages own swipe actions and context menus. Cells export `initWithStyle:reuseIdentifier:`, so `RegisterClassForCellReuse` works.
- **TFM.** Use the same `net10.0-ios` TFM spelling as these libraries.
- **Mac requirement.** Mac machines now need the iOS workload, because `Wino.Editor.WebKit` restores both TFMs when no RID is given.
