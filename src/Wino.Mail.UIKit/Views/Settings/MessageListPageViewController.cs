using System.Collections.Specialized;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Message list: a live preview row, then the Windows MessageListPage sections (Message rows, Quick actions, Grouping and
/// conversations, Inbox). The phone list has one row density and no pointer, so density and hover actions are
/// unavailable (SettingAvailability: MailListDensity, HoverActions). The preview is the real mail row cell fed from the ViewModel's demo mail.
/// </summary>
public sealed class MessageListPageViewController(MessageListPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MessageListPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoMailRowCell _previewRow = new() { TranslatesAutoresizingMaskIntoConstraints = false, UserInteractionEnabled = false };
    private NSLayoutConstraint? _previewHeight;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        AddSection(Translator.IOS_SettingsSection_Preview, null, BuildPreview());

        var clearCache = Card(string.Empty, null, WinoIconGlyph.None, Bind.Button(Translator.SettingsMailList_ClearAvatarsCache_Button, vm.ClearAvatarsCacheCommand));
        var senderPictures = Expander(Translator.SettingsShowSenderPictures_Title, null, WinoIconGlyph.Person,
            Bind.Switch(p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled, (s, v) => s.IsShowSenderPicturesEnabled = v, Translator.SettingsShowSenderPictures_Title),
            clearCache);

        AddSection(Translator.SettingsMessageList_SectionRows, null,
            senderPictures,
            Card(Translator.SettingsShowPreviewText_Title, null, WinoIconGlyph.TextDescription,
                Bind.Switch(p, nameof(p.IsShowPreviewEnabled), s => s.IsShowPreviewEnabled, (s, v) => s.IsShowPreviewEnabled = v, Translator.SettingsShowPreviewText_Title)),
            Card(Translator.SettingsMailListCategoryIndicators_Title, Translator.SettingsMailListCategoryIndicators_Description, WinoIconGlyph.Tag,
                Bind.Switch(p, nameof(p.IsMailListCategoryIndicatorsEnabled), s => s.IsMailListCategoryIndicatorsEnabled, (s, v) => s.IsMailListCategoryIndicatorsEnabled = v, Translator.SettingsMailListCategoryIndicators_Title)),
            StackedCard(Translator.SettingsTimeFormat_Title, null, WinoIconGlyph.Clock,
                Bind.PopUp(vm, vm.TimeFormatPreferenceOptions, nameof(vm.SelectedTimeFormatPreferenceIndex), s => s.SelectedTimeFormatPreferenceIndex, (s, v) => s.SelectedTimeFormatPreferenceIndex = v)),
            StackedCard(Translator.SettingsShowAccountNickname_Title, null, WinoIconGlyph.PersonTag,
                Bind.PopUp(vm, vm.AccountNicknamePositionOptions, nameof(vm.SelectedAccountNicknamePositionIndex), s => s.SelectedAccountNicknamePositionIndex, (s, v) => s.SelectedAccountNicknamePositionIndex = v)));

        bool SwipeOn(IPreferencesService s) => s.IsSwipeActionsEnabled;
        AddSection(Translator.SettingsMessageList_SectionQuickActions, null,
            Expander(Translator.SettingsSwipeActions_Title, null, WinoIconGlyph.ArrowSwap,
                Bind.Switch(p, nameof(p.IsSwipeActionsEnabled), s => s.IsSwipeActionsEnabled, (s, v) => s.IsSwipeActionsEnabled = v, Translator.SettingsSwipeActions_Title),
                CustomisedAccountsNote(vm),
                Bind.Enabled(SwipeScopeCard(vm), p, nameof(p.IsSwipeActionsEnabled), SwipeOn),
                Bind.Enabled(StackedCard(Translator.SettingsSwipeActionLeft, null, WinoIconGlyph.None,
                    Bind.PopUp(vm, vm.AvailableSwipeActionsTranslations, nameof(vm.LeftSwipeActionIndex), s => s.LeftSwipeActionIndex, (s, v) => s.LeftSwipeActionIndex = v)),
                    p, nameof(p.IsSwipeActionsEnabled), SwipeOn),
                Bind.Enabled(StackedCard(Translator.SettingsSwipeActionRight, null, WinoIconGlyph.None,
                    Bind.PopUp(vm, vm.AvailableSwipeActionsTranslations, nameof(vm.RightSwipeActionIndex), s => s.RightSwipeActionIndex, (s, v) => s.RightSwipeActionIndex = v)),
                    p, nameof(p.IsSwipeActionsEnabled), SwipeOn)),
            Card(Translator.SettingsMailListActionBar_Title, null, WinoIconGlyph.Window,
                Bind.Switch(p, nameof(p.IsMailListActionBarEnabled), s => s.IsMailListActionBarEnabled, (s, v) => s.IsMailListActionBarEnabled = v, Translator.SettingsMailListActionBar_Title)));

        AddSection(Translator.SettingsMessageList_SectionGrouping, null,
            Card(Translator.SettingsMailListGroupHeaders_Title, null, WinoIconGlyph.GroupList,
                Bind.Switch(p, nameof(p.IsMailListGroupHeadersEnabled), s => s.IsMailListGroupHeadersEnabled, (s, v) => s.IsMailListGroupHeadersEnabled = v, Translator.SettingsMailListGroupHeaders_Title)),
            Bind.Enabled(StackedCard(Translator.SettingsMailListDateGroupingType_Title, null, WinoIconGlyph.CalendarWeek,
                Bind.PopUp(vm, vm.DateGroupingTypeOptions, nameof(vm.SelectedDateGroupingTypeIndex), s => s.SelectedDateGroupingTypeIndex, (s, v) => s.SelectedDateGroupingTypeIndex = v)),
                p, nameof(p.IsMailListGroupHeadersEnabled), s => s.IsMailListGroupHeadersEnabled),
            Expander(Translator.SettingsThreads_Title, null, WinoIconGlyph.TextBulletListTree, null,
                Card(Translator.SettingsThreads_Enabled_Title, null, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.IsThreadingEnabled), s => s.IsThreadingEnabled, (s, v) => s.IsThreadingEnabled = v, Translator.SettingsThreads_Enabled_Title)),
                Bind.Enabled(StackedCard(Translator.SettingsThreadOrder_Title, null, WinoIconGlyph.None,
                    Bind.PopUp(vm, vm.ThreadItemSortingOptions, nameof(vm.SelectedThreadItemSortingIndex), s => s.SelectedThreadItemSortingIndex, (s, v) => s.SelectedThreadItemSortingIndex = v)),
                    p, nameof(p.IsThreadingEnabled), s => s.IsThreadingEnabled)));

        AddSection(Translator.SettingsMessageList_SectionInbox, Translator.IOS_SettingsHint_OtherInboxNotice,
            Card(Translator.SettingsOtherInboxUnreadNotice_Title, null, WinoIconGlyph.Mail,
                Bind.Switch(p, nameof(p.IsOtherInboxUnreadNoticeEnabled), s => s.IsOtherInboxUnreadNoticeEnabled, (s, v) => s.IsOtherInboxUnreadNoticeEnabled = v, Translator.SettingsOtherInboxUnreadNotice_Title)),
            Card(Translator.SettingsMarkAsReadOnMoveToTrash_Title, null, WinoIconGlyph.Delete,
                Bind.Switch(p, nameof(p.IsMarkAsReadOnMoveToTrashEnabled), s => s.IsMarkAsReadOnMoveToTrashEnabled, (s, v) => s.IsMarkAsReadOnMoveToTrashEnabled = v, Translator.SettingsMarkAsReadOnMoveToTrash_Title)));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter);

    private UIView BuildPreview()
    {
        var previewHeight = _previewRow.HeightAnchor.ConstraintEqualTo(0);
        previewHeight.Active = true;
        _previewHeight = previewHeight;
        _previewRow.AccessibilityLabel = Translator.SettingsMailListPreview_Title;

        // The section header names the preview, so the card holds only the row, spanning it like a list row.
        var card = new WinoSurfaceView { Fill = WinoSettingsStyle.GroupFill, Stroke = WinoSettingsStyle.GroupStroke, StrokeWidth = 1, CornerRadius = WinoSettingsStyle.CardRadius, ClipsToBounds = true };
        WinoLayout.Fill(_previewRow, card, 4, 0, 4, 0);

        NotifyCollectionChangedEventHandler changed = (_, _) => InvokeOnMainThread(RefreshPreview);
        ViewModel.PreviewMailCollection.Items.CollectionChanged += changed;
        Bindings.Own(new ActionDisposable(() => ViewModel.PreviewMailCollection.Items.CollectionChanged -= changed));
        RefreshPreview();
        return card;
    }

    private void RefreshPreview()
    {
        var item = ViewModel.PreviewMailCollection.GetFirst();
        _previewRow.Hidden = item is null;
        if (item is null) return;

        var model = MailRowMapper.TryMap(MailListRow.Single(item), ViewModel.PreferencesService, showAccountColor: false);
        if (model is null) { _previewRow.Hidden = true; return; }

        _previewRow.Apply(model);
        if (_previewHeight is { } height) height.Constant = (nfloat)WinoMailRowModel.HeightFor(model.Density, model.Tiles, 0);
    }

    /// <summary>
    /// "All accounts" or one account whose swipe actions the pickers below edit, with "Use all accounts" while that
    /// account carries its own (Windows MessageListPage scope card). Hidden with a single account and no override.
    /// </summary>
    private UIView SwipeScopeCard(MessageListPageViewModel vm)
    {
        var popup = Bind.PopUp<MessageListPageViewModel, string>(vm, s => s.ActionScopeOptions, option => option, nameof(vm.SelectedSwipeScopeIndex),
            s => s.SelectedSwipeScopeIndex >= 0 && s.SelectedSwipeScopeIndex < s.ActionScopeOptions.Count ? s.ActionScopeOptions[s.SelectedSwipeScopeIndex] : null,
            (s, option) => { var index = s.ActionScopeOptions.IndexOf(option); if (index >= 0) s.SelectedSwipeScopeIndex = index; },
            itemsProperty: nameof(vm.ActionScopeOptions));
        var useDefaults = Bind.Button(Translator.SettingsActionScope_UseAllAccounts, vm.UseDefaultSwipeActionsCommand);
        Bind.Visible(useDefaults, vm, nameof(vm.CanUseDefaultSwipeActions), s => s.CanUseDefaultSwipeActions);
        var card = StackedCard(Translator.SettingsActionScope_Title, vm.SwipeScopeDescription, WinoIconGlyph.None, Row(popup, useDefaults));
        Bind.Bind(vm, nameof(vm.SwipeScopeDescription), s => s.SwipeScopeDescription, description => card.Description = description);
        Bind.Visible(card, vm, nameof(vm.IsAccountScopeAvailable), s => s.IsAccountScopeAvailable);
        return card;
    }

    /// <summary>The accounts that carry their own swipe actions, shown while any does.</summary>
    private UIView CustomisedAccountsNote(MessageListPageViewModel vm)
    {
        var note = Bind.Label(vm, nameof(vm.CustomisedSwipeAccountsText), s => s.CustomisedSwipeAccountsText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var card = Card(string.Empty, null, WinoIconGlyph.None, note);
        Bind.Visible(card, vm, nameof(vm.HasCustomisedSwipeAccounts), s => s.HasCustomisedSwipeAccounts);
        return card;
    }
}
