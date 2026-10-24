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
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Message list: a live preview row, sender pictures, swipe actions, threads and the remaining row options (Windows
/// MessageListPage, same card order). The phone list has one row density and no pointer, so density and hover actions are
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

        AddGroup(null, BuildPreview());

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
        bool SwipeOn(IPreferencesService s) => s.IsSwipeActionsEnabled;
        AddGroup(null, Expander(Translator.SettingsSwipeActions_Title, Translator.SettingsSwipeActions_Description, WinoIconGlyph.ArrowSwap,
            Bind.Switch(p, nameof(p.IsSwipeActionsEnabled), s => s.IsSwipeActionsEnabled, (s, v) => s.IsSwipeActionsEnabled = v, Translator.SettingsSwipeActions_Title),
            CustomisedAccountsNote(vm),
            Bind.Enabled(SwipeScopeCard(vm), p, nameof(p.IsSwipeActionsEnabled), SwipeOn),
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

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter);

    private UIView BuildPreview()
    {
        var title = WinoStyle.Label(Translator.SettingsMailListPreview_Title, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var previewHeight = _previewRow.HeightAnchor.ConstraintEqualTo(0);
        previewHeight.Active = true;
        _previewHeight = previewHeight;
        _previewRow.AccessibilityLabel = Translator.SettingsMailListPreview_Title;

        // The title keeps the card padding while the row spans the card like a list row.
        var titleHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(title, titleHost, 0, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
        var stack = WinoLayout.VStack(WinoStyle.Space2, titleHost, _previewRow);
        stack.Alignment = UIStackViewAlignment.Fill;
        var card = new WinoSurfaceView { Fill = WinoSettingsStyle.GroupFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, CornerRadius = WinoSettingsStyle.CardRadius, ClipsToBounds = true };
        WinoLayout.Fill(stack, card, 12, 0, 8, 0);

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

        var model = Map(MailListRow.Single(item), ViewModel.PreferencesService);
        if (model is null) { _previewRow.Hidden = true; return; }

        _previewRow.Apply(model);
        if (_previewHeight is { } height) height.Constant = (nfloat)WinoMailRowModel.HeightFor(model.Density, model.Tiles, 0);
    }

    /// <summary>The shared row description with UIKit colours (the Mac MailRowMapper; the preview shows no account colour).</summary>
    private static WinoMailRowModel? Map(MailListRow row, IPreferencesService preferences)
    {
        if (MailRowPresentation.Describe(row, preferences.MailTimeFormatPreference) is not { } content) return null;

        var tiles = new List<WinoMailRowTile>();
        if (MailRowPresentation.HasTiles(row))
        {
            var leaves = MailRowPresentation.Leaves(row);
            foreach (var category in leaves.SelectMany(static leaf => leaf.Categories).Where(static category => category is not null).DistinctBy(static category => category.Id))
                tiles.Add(new WinoMailRowTile(category.Name ?? string.Empty, null, WinoStyle.FromHexString(category.BackgroundColorHex), WinoStyle.FromHexString(category.TextColorHex), false));
            foreach (var tile in leaves.Where(static leaf => leaf.HasRowIntelligenceTiles).SelectMany(static leaf => leaf.RowIntelligenceTiles).DistinctBy(static tile => tile.Text + tile.Glyph))
                tiles.Add(new WinoMailRowTile(tile.Text ?? string.Empty, tile.Glyph, null, null, true, tile.IsWarning, tile.AccessibleText));
        }

        return new WinoMailRowModel
        {
            Kind = content.Kind,
            Sender = content.Sender,
            SenderAddress = content.SenderAddress,
            Subject = content.Subject,
            Preview = content.Preview,
            DateText = content.DateText,
            AccessibleDateText = content.AccessibleDateText,
            IsUnread = content.IsUnread,
            IsFlagged = content.IsFlagged,
            IsPinned = content.IsPinned,
            IsDraft = content.IsDraft,
            IsBusy = content.IsBusy,
            HasAttachments = content.HasAttachments,
            Tiles = tiles,
            Density = MailRowPresentation.Density(preferences.MailItemDisplayMode),
            ThreadCount = content.ThreadCount,
            IsThreadExpanded = content.IsThreadExpanded,
            AccountNickname = content.AccountNickname,
            NicknamePosition = preferences.AccountNicknamePosition,
            ShowPicture = preferences.IsShowSenderPicturesEnabled,
            ShowPreview = preferences.IsShowPreviewEnabled,
            DraftLabel = Translator.Draft,
            AccessibilityText = content.AccessibilityText
        };
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
