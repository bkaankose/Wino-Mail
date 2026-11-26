using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Message list in the macOS/Windows sections: density and sender pictures, then Rows, Quick actions, Grouping and Inbox.
/// The live preview row of Windows and macOS is not built.
/// </summary>
internal sealed class MessageListPage(MessageListPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<MessageListPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        var clearCache = WinoSettingsRows.Action(string.Empty);
        WinoSettingsRows.Suffix(clearCache, Settings.Button(Translator.SettingsMailList_ClearAvatarsCache_Button, vm.ClearAvatarsCacheCommand));
        WinoA11y.Label(clearCache, Translator.SettingsMailList_ClearAvatarsCache_Button);

        var senderPictures = WinoSettingsRows.Expander(Translator.SettingsShowSenderPictures_Title, Translator.SettingsShowSenderPictures_Description, WinoIconGlyph.Person, false,
            Settings.Enabled(Settings.Switch(p, Translator.SettingsEnableGravatarAvatars_Title, Translator.SettingsEnableGravatarAvatars_Description, WinoIconGlyph.None,
                    nameof(p.IsGravatarEnabled), static s => s.IsGravatarEnabled, static (s, v) => s.IsGravatarEnabled = v),
                p, nameof(p.IsShowSenderPicturesEnabled), static s => s.IsShowSenderPicturesEnabled),
            Settings.Enabled(Settings.Switch(p, Translator.SettingsEnableFavicons_Title, Translator.SettingsEnableFavicons_Description, WinoIconGlyph.None,
                    nameof(p.IsFaviconEnabled), static s => s.IsFaviconEnabled, static (s, v) => s.IsFaviconEnabled = v),
                p, nameof(p.IsShowSenderPicturesEnabled), static s => s.IsShowSenderPicturesEnabled),
            clearCache);
        WinoSettingsRows.Suffix(senderPictures, Settings.SwitchSuffix(p, Translator.SettingsShowSenderPictures_Title, nameof(p.IsShowSenderPicturesEnabled),
            static s => s.IsShowSenderPicturesEnabled, static (s, v) => s.IsShowSenderPicturesEnabled = v));

        AddGroup(null,
            vm.IsMailListDensityAvailable
                ? Settings.Choice(vm, Translator.SettingsMailSpacing_Title, Translator.SettingsMailSpacing_Description, WinoIconGlyph.TextLineSpacing, [.. vm.MailSpacingOptions],
                    nameof(vm.SelectedMailSpacingIndex), static s => s.SelectedMailSpacingIndex, static (s, v) => s.SelectedMailSpacingIndex = v)
                : null,
            senderPictures);

        AddGroup(Translator.SettingsMessageList_SectionRows,
            Settings.Switch(p, Translator.SettingsShowPreviewText_Title, Translator.SettingsShowPreviewText_Description, WinoIconGlyph.TextDescription,
                nameof(p.IsShowPreviewEnabled), static s => s.IsShowPreviewEnabled, static (s, v) => s.IsShowPreviewEnabled = v),
            Settings.Switch(p, Translator.SettingsMailListCategoryIndicators_Title, Translator.SettingsMailListCategoryIndicators_Description, WinoIconGlyph.Tag,
                nameof(p.IsMailListCategoryIndicatorsEnabled), static s => s.IsMailListCategoryIndicatorsEnabled, static (s, v) => s.IsMailListCategoryIndicatorsEnabled = v),
            Settings.Choice(vm, Translator.SettingsTimeFormat_Title, Translator.SettingsTimeFormat_Description, WinoIconGlyph.Clock, [.. vm.TimeFormatPreferenceOptions],
                nameof(vm.SelectedTimeFormatPreferenceIndex), static s => s.SelectedTimeFormatPreferenceIndex, static (s, v) => s.SelectedTimeFormatPreferenceIndex = v),
            Settings.Choice(vm, Translator.SettingsShowAccountNickname_Title, Translator.SettingsShowAccountNickname_Description, WinoIconGlyph.PersonTag, [.. vm.AccountNicknamePositionOptions],
                nameof(vm.SelectedAccountNicknamePositionIndex), static s => s.SelectedAccountNicknamePositionIndex, static (s, v) => s.SelectedAccountNicknamePositionIndex = v));

        AddGroup(Translator.SettingsMessageList_SectionQuickActions,
            vm.IsHoverActionsAvailable ? HoverActions(vm, p) : null,
            SwipeActions(vm, p),
            Settings.Switch(p, Translator.SettingsMailListActionBar_Title, Translator.SettingsMailListActionBar_Description, WinoIconGlyph.Window,
                nameof(p.IsMailListActionBarEnabled), static s => s.IsMailListActionBarEnabled, static (s, v) => s.IsMailListActionBarEnabled = v));

        AddGroup(Translator.SettingsMessageList_SectionGrouping,
            Settings.Switch(p, Translator.SettingsMailListGroupHeaders_Title, Translator.SettingsMailListGroupHeaders_Description, WinoIconGlyph.GroupList,
                nameof(p.IsMailListGroupHeadersEnabled), static s => s.IsMailListGroupHeadersEnabled, static (s, v) => s.IsMailListGroupHeadersEnabled = v),
            Settings.Enabled(Settings.Choice(vm, Translator.SettingsMailListDateGroupingType_Title, Translator.SettingsMailListDateGroupingType_Description, WinoIconGlyph.CalendarWeek, [.. vm.DateGroupingTypeOptions],
                    nameof(vm.SelectedDateGroupingTypeIndex), static s => s.SelectedDateGroupingTypeIndex, static (s, v) => s.SelectedDateGroupingTypeIndex = v),
                p, nameof(p.IsMailListGroupHeadersEnabled), static s => s.IsMailListGroupHeadersEnabled),
            WinoSettingsRows.Expander(Translator.SettingsThreads_Title, Translator.SettingsThreads_Description, WinoIconGlyph.TextBulletListTree, false,
                Settings.Switch(p, Translator.SettingsThreads_Enabled_Title, Translator.SettingsThreads_Enabled_Description, WinoIconGlyph.None,
                    nameof(p.IsThreadingEnabled), static s => s.IsThreadingEnabled, static (s, v) => s.IsThreadingEnabled = v),
                Settings.Enabled(Settings.Choice(vm, Translator.SettingsThreadOrder_Title, Translator.SettingsThreadOrder_Description, WinoIconGlyph.None, [.. vm.ThreadItemSortingOptions],
                        nameof(vm.SelectedThreadItemSortingIndex), static s => s.SelectedThreadItemSortingIndex, static (s, v) => s.SelectedThreadItemSortingIndex = v),
                    p, nameof(p.IsThreadingEnabled), static s => s.IsThreadingEnabled)));

        AddGroup(Translator.SettingsMessageList_SectionInbox,
            Settings.Switch(p, Translator.SettingsOtherInboxUnreadNotice_Title, Translator.SettingsOtherInboxUnreadNotice_Description, WinoIconGlyph.Mail,
                nameof(p.IsOtherInboxUnreadNoticeEnabled), static s => s.IsOtherInboxUnreadNoticeEnabled, static (s, v) => s.IsOtherInboxUnreadNoticeEnabled = v),
            Settings.Switch(p, Translator.SettingsMarkAsReadOnMoveToTrash_Title, Translator.SettingsMarkAsReadOnMoveToTrash_Description, WinoIconGlyph.Delete,
                nameof(p.IsMarkAsReadOnMoveToTrashEnabled), static s => s.IsMarkAsReadOnMoveToTrashEnabled, static (s, v) => s.IsMarkAsReadOnMoveToTrashEnabled = v));
    }

    private Adw.ExpanderRow HoverActions(MessageListPageViewModel vm, IPreferencesService p)
    {
        Gtk.Widget HoverEnabled(Gtk.Widget row) => Settings.Enabled(row, p, nameof(p.IsHoverActionsEnabled), static s => s.IsHoverActionsEnabled);
        string[] actions = [.. vm.AvailableHoverActionsTranslations];

        var expander = WinoSettingsRows.Expander(Translator.SettingsHoverActions_Title, Translator.SettingsHoverActions_Description, WinoIconGlyph.CursorHover, false,
            CustomisedNote(vm, swipe: false),
            HoverEnabled(ScopeRow(vm, swipe: false)),
            HoverEnabled(Settings.Choice(vm, Translator.SettingsHoverActionLeft, null, WinoIconGlyph.None, actions,
                nameof(vm.LeftHoverActionIndex), static s => s.LeftHoverActionIndex, static (s, v) => s.LeftHoverActionIndex = v)),
            HoverEnabled(Settings.Choice(vm, Translator.SettingsHoverActionCenter, null, WinoIconGlyph.None, actions,
                nameof(vm.CenterHoverActionIndex), static s => s.CenterHoverActionIndex, static (s, v) => s.CenterHoverActionIndex = v)),
            HoverEnabled(Settings.Choice(vm, Translator.SettingsHoverActionRight, null, WinoIconGlyph.None, actions,
                nameof(vm.RightHoverActionIndex), static s => s.RightHoverActionIndex, static (s, v) => s.RightHoverActionIndex = v)),
            Presentation(vm, Settings.Choice(vm, Translator.SettingsHoverActionButtonSize, null, WinoIconGlyph.None, [.. vm.HoverActionButtonSizeOptions],
                nameof(vm.SelectedHoverActionButtonSizeIndex), static s => s.SelectedHoverActionButtonSizeIndex, static (s, v) => s.SelectedHoverActionButtonSizeIndex = v)),
            Presentation(vm, Settings.Choice(vm, Translator.SettingsHoverActionPosition, null, WinoIconGlyph.None, [.. vm.HoverActionPositionOptions],
                nameof(vm.SelectedHoverActionPositionIndex), static s => s.SelectedHoverActionPositionIndex, static (s, v) => s.SelectedHoverActionPositionIndex = v)),
            Presentation(vm, Settings.Choice(vm, Translator.SettingsHoverActionAnimation, null, WinoIconGlyph.None, [.. vm.HoverActionAnimationOptions],
                nameof(vm.SelectedHoverActionAnimationIndex), static s => s.SelectedHoverActionAnimationIndex, static (s, v) => s.SelectedHoverActionAnimationIndex = v)));
        WinoSettingsRows.Suffix(expander, Settings.SwitchSuffix(p, Translator.SettingsHoverActions_Title, nameof(p.IsHoverActionsEnabled),
            static s => s.IsHoverActionsEnabled, static (s, v) => s.IsHoverActionsEnabled = v));
        return expander;
    }

    private Adw.ExpanderRow SwipeActions(MessageListPageViewModel vm, IPreferencesService p)
    {
        Gtk.Widget SwipeEnabled(Gtk.Widget row) => Settings.Enabled(row, p, nameof(p.IsSwipeActionsEnabled), static s => s.IsSwipeActionsEnabled);
        string[] actions = [.. vm.AvailableSwipeActionsTranslations];

        var expander = WinoSettingsRows.Expander(Translator.SettingsSwipeActions_Title, Translator.SettingsSwipeActions_Description, WinoIconGlyph.ArrowSwap, false,
            CustomisedNote(vm, swipe: true),
            SwipeEnabled(ScopeRow(vm, swipe: true)),
            SwipeEnabled(Settings.Choice(vm, Translator.SettingsSwipeActionLeft, null, WinoIconGlyph.None, actions,
                nameof(vm.LeftSwipeActionIndex), static s => s.LeftSwipeActionIndex, static (s, v) => s.LeftSwipeActionIndex = v)),
            SwipeEnabled(Settings.Choice(vm, Translator.SettingsSwipeActionRight, null, WinoIconGlyph.None, actions,
                nameof(vm.RightSwipeActionIndex), static s => s.RightSwipeActionIndex, static (s, v) => s.RightSwipeActionIndex = v)));
        WinoSettingsRows.Suffix(expander, Settings.SwitchSuffix(p, Translator.SettingsSwipeActions_Title, nameof(p.IsSwipeActionsEnabled),
            static s => s.IsSwipeActionsEnabled, static (s, v) => s.IsSwipeActionsEnabled = v));
        return expander;
    }

    /// <summary>Size, position and animation are app-wide, so they lock while an account is selected (Windows MessageListPage).</summary>
    private Adw.ComboRow Presentation(MessageListPageViewModel vm, Adw.ComboRow row)
    {
        Settings.Subtitle(row, vm, nameof(vm.HoverPresentationDescription), static s => s.HoverPresentationDescription);
        return Settings.Enabled(row, vm, nameof(vm.IsHoverPresentationEditable), static s => s.IsHoverPresentationEditable);
    }

    /// <summary>"All accounts" or the one account whose hover or swipe actions the pickers below edit, as the macOS scope card.</summary>
    private Adw.ActionRow ScopeRow(MessageListPageViewModel vm, bool swipe)
    {
        var row = WinoSettingsRows.Action(Translator.SettingsActionScope_Title);
        Settings.Subtitle(row, vm, swipe ? nameof(vm.SwipeScopeDescription) : nameof(vm.HoverScopeDescription),
            s => swipe ? s.SwipeScopeDescription : s.HoverScopeDescription);

        var useDefaults = Settings.Button(Translator.SettingsActionScope_UseAllAccounts, swipe ? vm.UseDefaultSwipeActionsCommand : vm.UseDefaultHoverActionsCommand);
        Settings.Visible(useDefaults, vm, swipe ? nameof(vm.CanUseDefaultSwipeActions) : nameof(vm.CanUseDefaultHoverActions),
            s => swipe ? s.CanUseDefaultSwipeActions : s.CanUseDefaultHoverActions);
        WinoSettingsRows.Suffix(row, useDefaults);
        WinoSettingsRows.Suffix(row, Settings.ChoiceSuffix<MessageListPageViewModel, string>(vm, Translator.SettingsActionScope_Title,
            static s => s.ActionScopeOptions, static option => option,
            swipe ? nameof(vm.SelectedSwipeScopeIndex) : nameof(vm.SelectedHoverScopeIndex),
            s => ScopeOption(s, swipe ? s.SelectedSwipeScopeIndex : s.SelectedHoverScopeIndex),
            (s, option) =>
            {
                var index = s.ActionScopeOptions.IndexOf(option);
                if (index < 0) return;
                if (swipe) s.SelectedSwipeScopeIndex = index;
                else s.SelectedHoverScopeIndex = index;
            },
            itemsProperty: nameof(vm.ActionScopeOptions)));
        return Settings.Visible(row, vm, nameof(vm.IsAccountScopeAvailable), static s => s.IsAccountScopeAvailable);
    }

    private static string? ScopeOption(MessageListPageViewModel vm, int index)
        => index >= 0 && index < vm.ActionScopeOptions.Count ? vm.ActionScopeOptions[index] : null;

    private Adw.PreferencesRow CustomisedNote(MessageListPageViewModel vm, bool swipe)
    {
        var caption = WinoSettingsRows.Caption(null);
        Settings.Bind(vm, swipe ? nameof(vm.CustomisedSwipeAccountsText) : nameof(vm.CustomisedHoverAccountsText),
            s => swipe ? s.CustomisedSwipeAccountsText : s.CustomisedHoverAccountsText, text => caption.SetText(text ?? string.Empty));
        return Settings.Visible(WinoSettingsRows.Custom(caption), vm,
            swipe ? nameof(vm.HasCustomisedSwipeAccounts) : nameof(vm.HasCustomisedHoverAccounts),
            s => swipe ? s.HasCustomisedSwipeAccounts : s.HasCustomisedHoverAccounts);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);
}
