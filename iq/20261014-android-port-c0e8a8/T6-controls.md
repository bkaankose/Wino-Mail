# T6 Android control libraries (done, 14 Oct 2026)

## Files
- `controls/Wino.Presentation.Android` (net10.0-android, min 23): `WinoUnits` (Dp/Sp, `WinoViews.RequireContext/Require`, `WinoGraphics.Clamp`), `WinoInsets`, `WinoIcons` + `WinoGlyphDrawable` + `WinoIconView`, `WinoColors`, `WinoThemeSurfaces`, `WinoZoneLayout`, `WinoBackdrop`/`WinoBackdropView`/`WinoBarBackdropView`, `Bindings/ViewBinder`, `Bindings/BindingLifetime`. Resources: `wino_*` colours (light/night), dimens, `wino_zone_background`, `wino_divider`; assets `Fonts/WinoIcons*.ttf`.
- `controls/Wino.Mail.Controls.Android`: `Common/` (WinoContactPicture, WinoAccountIconView, WinoUnreadPill, WinoChipView + WinoChips, WinoInfoBar), `MailList/` (WinoMailRowModel/Tile/Sizes, WinoMailRowView, WinoMailGroupHeaderView, WinoMailListAdapter, WinoMailSwipeHelper), `Shell/WinoShellRowView`, `Settings/` (WinoSettingsCard, WinoSettingsExpander, WinoSettingsGroup, SettingsBinder). XML layouts `wino_mail_row`, `wino_mail_group_header`, `wino_shell_row`, `wino_settings_card`, `wino_info_bar`.
- Both in `WinoMail.Android.slnx`. Custom views carry `[Register("com.winomail.presentation|controls.X")]` for XML use.

## API shape
Same member names as the UIKit/AppKit twins: row `Apply(WinoMailRowModel)`, `IsSelectionMode/IsChecked/IsRowSelected`, `ThreadToggleRequested`, `CheckboxToggled`; shell row `ConfigureNewItem/Account/Folder/Header`, `SetSync`, `ShowsIndicator`; settings card `Header/Description/HeaderIcon/LeadingView/Content/BottomContent/IsClickable/ShowsChevron/IsExpanderHeader/IsExpanded/SetActionIcon/IsNested/IsEnabled/Activated`; expander `HeaderCard/IsExpanded/IsExpandedChanged/Add`; group `Title/Footer/Add/AddRange/Clear`; info bar `Severity/Title/Message/ActionTitle/IsClosable/AutoDismissInterval/ActionInvoked/Closed/RestartAutoDismiss` (+ `IsOpen`); SettingsBinder `Switch/CommandSwitch/PopUp/Button/Label` (+ `Views` for text/visible/enabled/checked/command).

`WinoMailListAdapter(map, groupTitle)`: `Attach(MailListProjection)`/`Detach`, flattens groups into strip + row entries keyed like iOS (`T:threadKey` / `I:id`), stable ids, projection changes throttled to 120 ms, DiffUtil when the change is under 40 entries else full rebind; rows re-map on leaf PropertyChanged (coalesced, main thread); selection keys, `SelectOnly/SetSelected/SelectAll/ClearSelection/SelectedRows`, `ToggleThread`, `RowClicked/RowLongClicked/SelectionChanged/EntriesChanged`, `RefreshRows()`. `WinoMailSwipeHelper.Attach(list, adapter, operation(row, leading) → MailOperation?, invoked)`: page decides the operation; tile uses `MailOperationDisplay`.

## Binding lifetime
`BindingLifetime.ForViewLifecycle(fragment)` disposes the scope on the view lifecycle's ON_DESTROY (rotation/theme recreation included); `UntilDetached(view)` for dialogs/code-built views. Recycled rows: the adapter disposes each row's subscription on rebind, recycle, `Detach` and `OnDetachedFromRecyclerView`. The head's `WinoFragment` keeps its own scope (unchanged).

## Shared moves / head changes
No shared code moved (no rule needed one; shared `MailRowPresentation`, `MailOperationDisplay`, `ContactPictureAppearance`, `WinoControlText`, `WinoListDiff`, `WinoAccountIconGlyphs`, `WinoMailRowMetrics` are used). Head: references both libraries; WinoIcons fonts and `wino_*` colour/dimen/zone resources moved to Presentation.Android (head keeps `ic_launcher_background`); `WinoIconFonts`/`SystemBarInsets` are now forwarding shims; `MainActivity.ApplyWallpaper` also calls `WinoBackdrop.Set`; Debug-only `Views/Debug/ControlsGalleryActivity` (`adb shell am start -n com.winomail.android/com.winomail.android.ControlsGallery [--ez dark true]`).

## Verification
- Both libraries Debug: 0 errors, 0 warnings from this code (only the existing Pkcs NU1902/1903).
- Head Debug build: succeeded, 0 errors. Install `-t:Install` succeeded.
- Gallery on Wino_Phone_API36 (my own emulator; the one running earlier had disappeared before I used it), light and dark, no crash in logcat: `T6-gallery-light.png`, `T6-gallery-light-2.png`, `T6-gallery-dark.png`, `T6-gallery-dark-2.png` (rows, strips, selection, tiles, shell rows, info bars, chips, avatars, buttons, settings cards and expander, blurred bar over the wallpaper). Swipe, tap and long press not exercised by hand.
- No shared code changed, so Controls/Core tests and Mac/iOS builds were not needed.

## Gaps for T7
- Page mapper `MailListRow → WinoMailRowModel` (from `MailRowPresentation.Describe` + account colour + sender picture), selection snapshot (`ApplyMailSelectionSnapshot`; the iOS `SyncSelectionFromTable` logic should move to Controls.Core rather than be copied a third time), load-more on scroll, ActionMode, context menus, pull to refresh.
- Theme service should set `WinoColors.AccentOverride`, `WinoThemeSurfaces.Apply`, `WinoIcons.Style` (T9). Remove the head shims once pages use the library directly.
- Settings binder lacks stepper/time picker/text field/segmented (add with T9 pages). Calendar surfaces not started (T10).
- TalkBack, font scale and RTL not checked manually. A WinoUnreadPill placed inside a Material ChipGroup clips the next chip row; keep the pill outside ChipGroups.
