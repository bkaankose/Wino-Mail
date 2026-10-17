using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Message list: sender pictures, swipe actions, threads and the remaining row options (Windows MessageListPage, iOS
/// MessageListPageViewController, same card order). The phone list has one row density and no pointer, so density and hover
/// actions are unavailable (SettingAvailability: MailListDensity, HoverActions). A settings page is a list of titles (owner, 15 Oct 2026), so the
/// Windows live preview row, which is not a setting, is not shown.
/// </summary>
public sealed class MessageListPageFragment : SettingsPageFragment<MessageListPageViewModel>
{
    protected override void BuildPage()
    {
        var vm = VM;
        var p = vm.PreferencesService;

        // Sender pictures
        var clearCache = Card(string.Empty, null, WinoIconGlyph.None, Bind.Button(Translator.SettingsMailList_ClearAvatarsCache_Button, vm.ClearAvatarsCacheCommand));
        var senderPictures = Expander(Translator.SettingsShowSenderPictures_Title, Translator.SettingsShowSenderPictures_Description, WinoIconGlyph.Person,
            Bind.Switch(p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled, (s, v) => s.IsShowSenderPicturesEnabled = v, Translator.SettingsShowSenderPictures_Title),
            Bind.Enabled(Card(Translator.SettingsEnableGravatarAvatars_Title, Translator.SettingsEnableGravatarAvatars_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsGravatarEnabled), s => s.IsGravatarEnabled, (s, v) => s.IsGravatarEnabled = v, Translator.SettingsEnableGravatarAvatars_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Bind.Enabled(Card(Translator.SettingsEnableFavicons_Title, Translator.SettingsEnableFavicons_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsFaviconEnabled), s => s.IsFaviconEnabled, (s, v) => s.IsFaviconEnabled = v, Translator.SettingsEnableFavicons_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            clearCache);

        AddGroup(null, senderPictures);

        // Swipe actions
        static bool SwipeOn(IPreferencesService s) => s.IsSwipeActionsEnabled;
        AddGroup(null, Expander(Translator.SettingsSwipeActions_Title, Translator.SettingsSwipeActions_Description, WinoIconGlyph.ArrowSwap,
            Bind.Switch(p, nameof(p.IsSwipeActionsEnabled), s => s.IsSwipeActionsEnabled, (s, v) => s.IsSwipeActionsEnabled = v, Translator.SettingsSwipeActions_Title),
            Bind.Enabled(StackedCard(Translator.SettingsSwipeActionLeft, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.AvailableSwipeActionsTranslations, nameof(vm.LeftSwipeActionIndex), s => s.LeftSwipeActionIndex, (s, v) => s.LeftSwipeActionIndex = v)),
                p, nameof(p.IsSwipeActionsEnabled), SwipeOn),
            Bind.Enabled(StackedCard(Translator.SettingsSwipeActionRight, null, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.AvailableSwipeActionsTranslations, nameof(vm.RightSwipeActionIndex), s => s.RightSwipeActionIndex, (s, v) => s.RightSwipeActionIndex = v)),
                p, nameof(p.IsSwipeActionsEnabled), SwipeOn)));

        AddGroup(null,
            Card(Translator.SettingsMailListActionBar_Title, Translator.SettingsMailListActionBar_Description, WinoIconGlyph.Window,
                Bind.Switch(p, nameof(p.IsMailListActionBarEnabled), s => s.IsMailListActionBarEnabled, (s, v) => s.IsMailListActionBarEnabled = v, Translator.SettingsMailListActionBar_Title)),
            StackedCard(Translator.SettingsShowAccountNickname_Title, Translator.SettingsShowAccountNickname_Description, WinoIconGlyph.PersonTag,
                Bind.PopUp(vm, vm.AccountNicknamePositionOptions, nameof(vm.SelectedAccountNicknamePositionIndex), s => s.SelectedAccountNicknamePositionIndex, (s, v) => s.SelectedAccountNicknamePositionIndex = v)),
            Card(Translator.SettingsMailListGroupHeaders_Title, Translator.SettingsMailListGroupHeaders_Description, WinoIconGlyph.GroupList,
                Bind.Switch(p, nameof(p.IsMailListGroupHeadersEnabled), s => s.IsMailListGroupHeadersEnabled, (s, v) => s.IsMailListGroupHeadersEnabled = v, Translator.SettingsMailListGroupHeaders_Title)),
            StackedCard(Translator.SettingsTimeFormat_Title, Translator.SettingsTimeFormat_Description, WinoIconGlyph.Clock,
                Bind.PopUp(vm, vm.TimeFormatPreferenceOptions, nameof(vm.SelectedTimeFormatPreferenceIndex), s => s.SelectedTimeFormatPreferenceIndex, (s, v) => s.SelectedTimeFormatPreferenceIndex = v)),
            Card(Translator.SettingsOtherInboxUnreadNotice_Title, Translator.SettingsOtherInboxUnreadNotice_Description, WinoIconGlyph.Mail,
                Bind.Switch(p, nameof(p.IsOtherInboxUnreadNoticeEnabled), s => s.IsOtherInboxUnreadNoticeEnabled, (s, v) => s.IsOtherInboxUnreadNoticeEnabled = v, Translator.SettingsOtherInboxUnreadNotice_Title)));

        // Threads
        AddGroup(null, Expander(Translator.SettingsThreads_Title, Translator.SettingsThreads_Description, WinoIconGlyph.TextBulletListTree, null,
            Card(Translator.SettingsThreads_Enabled_Title, Translator.SettingsThreads_Enabled_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.IsThreadingEnabled), s => s.IsThreadingEnabled, (s, v) => s.IsThreadingEnabled = v, Translator.SettingsThreads_Enabled_Title)),
            Bind.Enabled(StackedCard(Translator.SettingsThreadOrder_Title, Translator.SettingsThreadOrder_Description, WinoIconGlyph.None,
                Bind.PopUp(vm, vm.ThreadItemSortingOptions, nameof(vm.SelectedThreadItemSortingIndex), s => s.SelectedThreadItemSortingIndex, (s, v) => s.SelectedThreadItemSortingIndex = v)),
                p, nameof(p.IsThreadingEnabled), s => s.IsThreadingEnabled)));

        AddGroup(null,
            Card(Translator.SettingsShowPreviewText_Title, Translator.SettingsShowPreviewText_Description, WinoIconGlyph.TextDescription,
                Bind.Switch(p, nameof(p.IsShowPreviewEnabled), s => s.IsShowPreviewEnabled, (s, v) => s.IsShowPreviewEnabled = v, Translator.SettingsShowPreviewText_Title)));
    }
}
