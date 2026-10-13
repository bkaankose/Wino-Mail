# C: presentation rules into shared code (T5)

Worktree: `D:\Wino-Mail\.claude\worktrees\agent-a070b0cee07d5f1fa` (branch `worktree-agent-a070b0cee07d5f1fa`). Everything is uncommitted. Mac mirror: `~/Desktop/wt-C`.

The Windows PC restarted during the run. Two files had been zero-filled (`WinoMiniCalendarView.cs`, `WinoMiniCalendarGrid.cs`). I rewrote the first and restored the second from HEAD, then reapplied the edits. A scan of `src`, `controls` and `tests` found no other NUL bytes.

## Placement deviation

`controls/Wino.Mail.Controls.Core` has no reference to `Wino.Core.Domain`, and adding one would pull MimeKit, sqlite and the rest into the control core. So:

- Rules that need Domain types went to `Wino.Core.Domain`. That covers anything using `ICalendarItem`, `Translator` or the enums, including the calendar layout calculator, which sits next to the existing `CalendarEventPlacementCalculator`.
- Rules that need ViewModel types went to `Wino.Mail.ViewModels`. That covers `MailItemViewModel`, `ContactFilterViewModel` and the calendar menu items.
- Pure math went to Controls.Core: row metrics, the mini-calendar grid, the avatar palette and the intelligence header state.

Calendar layout tests are therefore in `tests/Wino.Core.Tests`, not the Controls tests.

## Per item

| # | Shared type | Mac consumer | WinUI |
|---|---|---|---|
| 1 | `Domain/Models/Personalization/WinoThemeCatalog.cs` (`WinoThemeDefinition`, `WinoThemeGradient`, 21 themes, keys, `GradientFor`, `CompatibilityFor`) | `MacWinoThemeService` builds its themes and keys from the catalog. `GradientFor` stays as a `uint[]` adapter for the thumbnail and backdrop code. | `NewThemeService` builds `preDefinedThemes` and keys from the catalog. The 21 GUID fields are gone. `UnderlyingThemeService.SelectedAppThemeKey` points at the catalog. |
| 2 | `Domain/Presentation/MailOperationDisplay.cs`: `MailOperationDisplay` and `FolderOperationDisplay` (`GetTitle`, `GetGlyph`, `IsDestructive`, `AsksForInput`) | `MailOperationPresentation` and `FolderOperationPresentation` keep only the AppKit `Image` and `MenuTitle` helpers. Call sites use the shared title and glyph. | `XamlHelpers.GetPathGeometry(FolderOperation)` and `GetOperationString(FolderOperation)` delegate. Mail operations are a follow-up (see below). |
| 3 | `SpecialFolderGlyphs` (`GetGlyph`, `GetGlyphOrFolder`) in the same file. `Mail.ViewModels/Data/ShellMenuItemRules.cs` holds `SelectsOnInvoked`, `IsInteractive`, `IsHighlightable`, `IsEnabled`, `IsShown`, `ShowsIndicator`, `Children` and `IsAccountRow`. | `ShellPaneRows` keeps only heights. 7 Mac files call the shared rules. The folder glyph is used in 9 places, and 4 of them use `GetGlyphOrFolder`. | `GetSpecialFolderPathIconGeometry` delegates. The menu rules are a follow-up. |
| 4 | `MailFilterDisplay` (glyphs, the `MailFilterActionTone` enum, names, `GetSummary`, `IsExclusiveAction`), `MailProviderGlyphs`, `ContactDisplayFormatter.FormatBirthday` (also guards years outside 1–9999), `SettingsPageAvailability` (in `Models/Settings`). `WinoAccountAuthErrorTranslator` and `WinoAccountEmailConfirmationHelper` moved from WinUI into Domain (git mv, now public); the moved `errorMessage!` is now `?? string.Empty`. | `MailFilterPresentation` keeps only the tone colour and the tile. The editor's exclusive rule uses the shared one. `ProviderCard.Glyph` delegates, with a None→IMAP fallback. Contacts uses the birthday formatter. `WinoAccountErrorText.cs` is deleted, and the account sheets use the shared helpers. `SettingsPageCatalog.IsVisible` uses `SettingsPageAvailability`. | `XamlHelpers` delegates `GetFilterFieldGlyph`, `GetFilterActionGlyph`, `FormatBirthday` and `GetProviderIcon`. The WinUI dialogs use the moved helpers with no code change. `MailFilterEditorPageViewModel.IsExclusiveLocalAction` delegates. `SettingsMenuProvider` uses `SettingsPageAvailability` (behaviour unchanged, because Intelligence is a top-level node). |
| 5 | `Mail.ViewModels/Collections/MailRowPresentation.cs` holds `MailRowContent`, `Density`, `Kind`, `Leaves`, `HasTiles`, `Describe`, `SubjectForListing`, `FormatListDate` (today's time through `DateTimeDisplayFormatter` with the user's time preference), `FormatAccessibleDate`, `FormatReaderDate` (same formatter) and `GroupTitle`. `Domain/Presentation/CalendarEventDisplay.cs` holds `CalendarDragRefusal`, `GetDragRefusal(CalendarItem?, isBusy)`, `IsReadOnly`, `FormatDateRange`, `FormatRecurrence` (through a new `CalendarRecurrenceRuleParser` and `CalendarRecurrenceSummaryFormatter`) and `GetAttendeeStatusText`. | `MailRowMapper` is now a thin colour and tile mapper. The reader passes `PreferencesService.MailTimeFormatPreference`. `CalendarTileMapper` delegates, and its private `CalendarDragRefusal` enum is removed. | Follow-up (see below). |
| 6 | `Domain/Models/Calendar/CalendarSurfaceLayoutCalculator.cs` with `CalendarLayoutRect`, `CalendarLayoutPlacement`, `CalendarLayoutMonthCell` and `CalendarPlacementKind` (moved from AppKit) | The AppKit `CalendarLayoutCalculator` is now a CGRect adapter with the same API, so the surface code is unchanged. | Follow-up: `TimedCalendarLayout` adds `ItemRightSpacing` back in overlap modes and carries templates, so the swap is not mechanical. |
| 7 | `Controls.Core/MailRow/WinoMailRowMetrics.cs` (the `WinoMailRowDensity` and `WinoMailRowKind` enums move here; constants, `TextWidth`, `TileLines(widths)`, `HeightFor(density, tileLines, lineHeight delegate)`, `GroupRowHeight`). `IntelligenceHeader/WinoIntelligenceHeaderState.cs` moved as is, with `Array.Empty` in `Empty`. `ToDo/WinoTaskRowModel.cs` takes `ListColorHex` instead of `NSColor`. | The AppKit `WinoMailRowModel.HeightFor` wraps the shared metrics. The new `WinoMailRowMeasure` holds the AppKit `LineHeight`, `TileWidth`, `TileLines`, `SameTileLayout` and `GroupRowHeight`. The task card parses the hex. The ToDo preview colours are hex. | Not applicable: WinUI uses XAML templates. |
| 8 | `Controls.Core/ContactPictureAppearance.cs` (16-step palette as `uint`, `PaletteIndex`, `PaletteColor`, `PaletteColorHex`, `Initials`). `Domain/Presentation/ReadableTextColor.cs` covers both rules: perceived brightness > 0.6 (Mac and calendar) and WCAG luminance > 0.5 (WinUI). `Controls.Core/Calendar/WinoMiniCalendarGrid.cs` (heights, grid start and dates, hit test, cell bounds, focus clamp, selection run ends). | `WinoContactPicture` uses the shared initials and `WinoStyle.Hex(PaletteColor)`. `WinoStyle` is untouched. `WinoCalendarItemView.ReadableTextColor` and `WinoMiniCalendarView` use the shared helpers. | `XamlHelpers.GetReadableTextColor` uses `PrefersDarkTextByLuminance`. |

Note on item 8: the Mac calendar tile formula was the perceived-brightness rule (0.299/0.587/0.114 > 0.6), not the WinUI luminance rule. Both rules are kept, under separate names.

## WinUI follow-ups (not mechanical)

- `XamlHelpers.GetWinoIconGlyph(MailOperation)` and `GetOperationString(MailOperation)` differ from the shared map:
  - Move uses Forward on WinUI and Move on Mac.
  - Focused/Other, AlwaysMove and RetryDraftUpload have no glyph on WinUI.
  - None and default return "unknown" on WinUI.

  Pick one set before WinUI delegates.
- `CalendarXamlHelpers.GetEventDetailsDateString` has no all-day branch. `GetRecurrenceString` uses Ical.Net. `GetAttendeeStatusText` maps unknown values to "".
- WinUI `TimedCalendarLayout` and `MonthCalendarLayout` → `CalendarSurfaceLayoutCalculator`.
- The shell pane templates' SelectsOnInvoked and visibility rules → `ShellMenuItemRules`.
- WinUI `ContactVisual` (FNV-1a hash, own initials) differs from `ContactPictureAppearance`.
- Outside my scope:
  - `Wino.Services/MailFilterService.IsTerminal` duplicates `MailFilterDisplay.IsExclusiveAction`.
  - `Wino.Core/Misc/ColorHelpers.GetReadableTextColorHex` duplicates `ReadableTextColor.PrefersDarkText`.
  - Agent D can redirect `WinoStyle.AvatarPalette` and `AvatarColor` to `ContactPictureAppearance`.

## Behaviour changes (intended)

- On the Mac, the list time for today and the reader time now follow the user's 12/24-hour preference through `DateTimeDisplayFormatter`.
- The Mac event details recurrence line is now the full Windows summary sentence instead of the FREQ-only phrase. It falls back to "Recurring" when the rule cannot be parsed or settings are missing.

## Tests added

- `tests/Wino.Mail.Controls.Tests/NativePresentationMathTests.cs` (17 cases): row heights per density, tile wrapping and the cap, text width, group height, mini-calendar grid start, height, hit test, cell bounds, focus clamp and selection runs, avatar palette index and initials, intelligence header state.
- `tests/Wino.Core.Tests/Presentation/SharedPresentationRulesTests.cs` (37 cases including theory rows): theme catalog, operation, folder and provider maps, filter rules, settings availability, birthday, readable text colour, calendar layout (stacked columns, all-day lanes, month grid and packing), RRULE parser, drag refusal.

## Verification (Windows host; logs in `D:\Wino-Mail\artifacts\ios-platform\C-*.log`)

| Command | Result |
|---|---|
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64` | Passed, 0 errors (`C-winui-debug.log`) |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` | Passed, 0 errors and 0 warnings (`C-winui-release.log`) |
| `dotnet test tests/Wino.Mail.Controls.Tests -c Debug -p:Platform=x64` | 51/51 passed (baseline 34) (`C-controls-tests.log`) |
| `dotnet test tests/Wino.Core.Tests -c Debug -p:Platform=x64` | 1079 passed, 1 failed (baseline 1042 + 1). The failure is the existing `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce` (`C-core-tests.log`). |
| Mac: `cd ~/Desktop/wt-C && ~/.dotnet/dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true -p:CodesignKey=- -p:CodesignProvision=` | Passed, 0 errors. The 48 warnings are existing ones (CA1422, CS0618, CS0067, …); none come from the changed files. (`C-mac-debug-arm64.log`) |

Mac mirror note: the Windows checkout stores the Sparkle and WinoStoreKit2 xcframework symlinks as text files, so the first Mac build failed with MT7091 after C# had compiled cleanly. I copied the two `.xcframework` folders from `~/Desktop/Wino-Mail` (read only) into `~/Desktop/wt-C`, and the rebuild passed. Other agents mirroring from Windows will hit the same thing.

XAML: no XAML changed, so the formatter was not run.

## Pending and risks

- Manual runtime checks have not been run on either platform:
  - **Windows:** theme gallery, folder and filter glyphs, birthday, category text colour.
  - **Mac:** theme gallery and backdrops, mail list rows and heights, reader date, folder and mail menus, filter editor, calendar week/month layout and drag refusal, event details date and recurrence, mini-calendar, avatars, ToDo list colours, Wino Account sign-in errors.
- No macOS x64 build was run (not requested).
- Agent D's move of `WinoStyle` to a tokens file must keep `WinoStyle.Hex` and `FromHexString`, which the edited AppKit files call.
