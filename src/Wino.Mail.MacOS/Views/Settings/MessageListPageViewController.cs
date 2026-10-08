using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Message list: sender pictures, density, hover and swipe actions, threads (Windows MessageListPage, same card order).</summary>
public sealed class MessageListPageViewController(MessageListPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MessageListPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        // Sender pictures
        var senderPictures = Expander(Translator.SettingsShowSenderPictures_Title, Translator.SettingsShowSenderPictures_Description, WinoIconGlyph.Person,
            Bind.Switch(p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled, (s, v) => s.IsShowSenderPicturesEnabled = v, Translator.SettingsShowSenderPictures_Title),
            Bind.Enabled(Card(Translator.SettingsEnableGravatarAvatars_Title, Translator.SettingsEnableGravatarAvatars_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsGravatarEnabled), s => s.IsGravatarEnabled, (s, v) => s.IsGravatarEnabled = v, Translator.SettingsEnableGravatarAvatars_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Bind.Enabled(Card(Translator.SettingsEnableFavicons_Title, Translator.SettingsEnableFavicons_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsFaviconEnabled), s => s.IsFaviconEnabled, (s, v) => s.IsFaviconEnabled = v, Translator.SettingsEnableFavicons_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Card(string.Empty, null, WinoIconGlyph.None, Bind.Button(Translator.SettingsMailList_ClearAvatarsCache_Button, vm.ClearAvatarsCacheCommand)));

        var density = Card(Translator.SettingsMailSpacing_Title, Translator.SettingsMailSpacing_Description, WinoIconGlyph.TextLineSpacing,
            Bind.Segmented(vm, vm.MailSpacingOptions, nameof(vm.SelectedMailSpacingIndex), s => s.SelectedMailSpacingIndex, (s, v) => s.SelectedMailSpacingIndex = v));

        AddGroup(null, senderPictures, density);

        // Hover actions
        bool HoverOn(IPreferencesService s) => s.IsHoverActionsEnabled;
        var hoverEnabled = Card(Translator.SettingsEnableHoverActions_Title, null, WinoIconGlyph.None,
            Bind.Switch(p, nameof(p.IsHoverActionsEnabled), s => s.IsHoverActionsEnabled, (s, v) => s.IsHoverActionsEnabled = v, Translator.SettingsEnableHoverActions_Title));
        AddGroup(null, Expander(Translator.SettingsHoverActions_Title, Translator.SettingsHoverActions_Description, WinoIconGlyph.CursorHover, null,
            hoverEnabled,
            Bind.Enabled(Card(Translator.SettingsHoverActionLeft, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.AvailableHoverActionsTranslations, nameof(vm.LeftHoverActionIndex), s => s.LeftHoverActionIndex, (s, v) => s.LeftHoverActionIndex = v, 150)),
                p, nameof(p.IsHoverActionsEnabled), HoverOn),
            Bind.Enabled(Card(Translator.SettingsHoverActionCenter, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.AvailableHoverActionsTranslations, nameof(vm.CenterHoverActionIndex), s => s.CenterHoverActionIndex, (s, v) => s.CenterHoverActionIndex = v, 150)),
                p, nameof(p.IsHoverActionsEnabled), HoverOn),
            Bind.Enabled(Card(Translator.SettingsHoverActionRight, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.AvailableHoverActionsTranslations, nameof(vm.RightHoverActionIndex), s => s.RightHoverActionIndex, (s, v) => s.RightHoverActionIndex = v, 150)),
                p, nameof(p.IsHoverActionsEnabled), HoverOn),
            Bind.Enabled(Card(Translator.SettingsHoverActionButtonSize, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.HoverActionButtonSizeOptions, nameof(vm.SelectedHoverActionButtonSizeIndex), s => s.SelectedHoverActionButtonSizeIndex, (s, v) => s.SelectedHoverActionButtonSizeIndex = v, 150)),
                p, nameof(p.IsHoverActionsEnabled), HoverOn),
            Bind.Enabled(Card(Translator.SettingsHoverActionPosition, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.HoverActionPositionOptions, nameof(vm.SelectedHoverActionPositionIndex), s => s.SelectedHoverActionPositionIndex, (s, v) => s.SelectedHoverActionPositionIndex = v, 150)),
                p, nameof(p.IsHoverActionsEnabled), HoverOn),
            Bind.Enabled(Card(Translator.SettingsHoverActionAnimation, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.HoverActionAnimationOptions, nameof(vm.SelectedHoverActionAnimationIndex), s => s.SelectedHoverActionAnimationIndex, (s, v) => s.SelectedHoverActionAnimationIndex = v, 150)),
                p, nameof(p.IsHoverActionsEnabled), HoverOn)));

        // Swipe actions
        bool SwipeOn(IPreferencesService s) => s.IsSwipeActionsEnabled;
        AddGroup(null, Expander(Translator.SettingsSwipeActions_Title, Translator.SettingsSwipeActions_Description, WinoIconGlyph.ArrowSwap,
            Bind.Switch(p, nameof(p.IsSwipeActionsEnabled), s => s.IsSwipeActionsEnabled, (s, v) => s.IsSwipeActionsEnabled = v, Translator.SettingsSwipeActions_Title),
            Bind.Enabled(Card(Translator.SettingsSwipeActionLeft, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.AvailableSwipeActionsTranslations, nameof(vm.LeftSwipeActionIndex), s => s.LeftSwipeActionIndex, (s, v) => s.LeftSwipeActionIndex = v, 150)),
                p, nameof(p.IsSwipeActionsEnabled), SwipeOn),
            Bind.Enabled(Card(Translator.SettingsSwipeActionRight, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.AvailableSwipeActionsTranslations, nameof(vm.RightSwipeActionIndex), s => s.RightSwipeActionIndex, (s, v) => s.RightSwipeActionIndex = v, 150)),
                p, nameof(p.IsSwipeActionsEnabled), SwipeOn)));

        AddGroup(null,
            Card(Translator.SettingsMailListActionBar_Title, Translator.SettingsMailListActionBar_Description, WinoIconGlyph.Window,
                Bind.Switch(p, nameof(p.IsMailListActionBarEnabled), s => s.IsMailListActionBarEnabled, (s, v) => s.IsMailListActionBarEnabled = v, Translator.SettingsMailListActionBar_Title)),
            Card(Translator.SettingsShowAccountNickname_Title, Translator.SettingsShowAccountNickname_Description, WinoIconGlyph.PersonTag,
                Bind.PopUp(vm, vm.AccountNicknamePositionOptions, nameof(vm.SelectedAccountNicknamePositionIndex), s => s.SelectedAccountNicknamePositionIndex, (s, v) => s.SelectedAccountNicknamePositionIndex = v, 170)),
            Card(Translator.SettingsMailListGroupHeaders_Title, Translator.SettingsMailListGroupHeaders_Description, WinoIconGlyph.GroupList,
                Bind.Switch(p, nameof(p.IsMailListGroupHeadersEnabled), s => s.IsMailListGroupHeadersEnabled, (s, v) => s.IsMailListGroupHeadersEnabled = v, Translator.SettingsMailListGroupHeaders_Title)),
            Card(Translator.SettingsTimeFormat_Title, Translator.SettingsTimeFormat_Description, WinoIconGlyph.Clock,
                Bind.PopUp(vm, vm.TimeFormatPreferenceOptions, nameof(vm.SelectedTimeFormatPreferenceIndex), s => s.SelectedTimeFormatPreferenceIndex, (s, v) => s.SelectedTimeFormatPreferenceIndex = v, 180)),
            Card(Translator.SettingsOtherInboxUnreadNotice_Title, Translator.SettingsOtherInboxUnreadNotice_Description, WinoIconGlyph.Mail,
                Bind.Switch(p, nameof(p.IsOtherInboxUnreadNoticeEnabled), s => s.IsOtherInboxUnreadNoticeEnabled, (s, v) => s.IsOtherInboxUnreadNoticeEnabled = v, Translator.SettingsOtherInboxUnreadNotice_Title)));

        // Threads
        AddGroup(null, Expander(Translator.SettingsThreads_Title, Translator.SettingsThreads_Description, WinoIconGlyph.TextBulletListTree, null,
            Card(Translator.SettingsThreads_Enabled_Title, Translator.SettingsThreads_Enabled_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.IsThreadingEnabled), s => s.IsThreadingEnabled, (s, v) => s.IsThreadingEnabled = v, Translator.SettingsThreads_Enabled_Title)),
            Bind.Enabled(Card(Translator.SettingsThreadOrder_Title, Translator.SettingsThreadOrder_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.ThreadItemSortingOptions, nameof(vm.SelectedThreadItemSortingIndex), s => s.SelectedThreadItemSortingIndex, (s, v) => s.SelectedThreadItemSortingIndex = v, 170)),
                p, nameof(p.IsThreadingEnabled), s => s.IsThreadingEnabled)));

        AddGroup(null,
            Card(Translator.SettingsShowPreviewText_Title, Translator.SettingsShowPreviewText_Description, WinoIconGlyph.TextDescription,
                Bind.Switch(p, nameof(p.IsShowPreviewEnabled), s => s.IsShowPreviewEnabled, (s, v) => s.IsShowPreviewEnabled = v, Translator.SettingsShowPreviewText_Title)));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);
}
