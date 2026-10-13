using System.ComponentModel;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Extras;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>
/// The toolbar row (the Mac ShellToolbar in the Windows title bar order): over the list column the sidebar toggle, sync
/// (a spinner while synchronizing) and the folder title with its account and unread subtitle; over the reader column the
/// search field routed to the content page's <see cref="IShellSearchTarget"/> (suggestions through
/// <see cref="IShellSearchSuggestionTarget"/>), Daily briefing with the unseen badge, What's New and the account avatar,
/// which opens Settings. The briefing panel is a right-edge overlay, 400pt wide, below the toolbar row.
/// </summary>
public sealed partial class PadShellViewController
{
    private const double BriefingPanelWidth = DailyBriefingPanelViewController.PanelWidth;

    private readonly UILabel _titleLabel = WinoStyle.Label(string.Empty, WinoStyle.Fixed(15, UIFontWeight.Semibold));
    private readonly UILabel _subtitleLabel = WinoStyle.Label(string.Empty, WinoStyle.Fixed(11), WinoStyle.SecondaryText);
    private readonly UISearchBar _search = new() { SearchBarStyle = UISearchBarStyle.Minimal, TranslatesAutoresizingMaskIntoConstraints = false };
    private UIView _titleView = null!;
    private UIBarButtonItem _syncItem = null!;
    private UIButton _syncButton = null!;
    private UIActivityIndicatorView _syncSpinner = null!;
    private UIBarButtonItem _briefingItem = null!;
    private UIButton _briefingButton = null!;
    private WinoSurfaceView _briefingDot = null!;
    private UIBarButtonItem _whatsNewItem = null!;
    private SearchDelegate? _searchDelegate;
    private IShellMenuProvider? _provider;
    private INotifyPropertyChanged? _titleSource;
    private IUIKitDailyBriefingPresenter? _briefing;
    private DailyBriefingPanelViewController? _briefingPanel;
    private UIView? _briefingHost;
    private NSLayoutConstraint? _briefingTop;
    private bool _briefingOpen;
    private bool _syncingSearchText;

    private void BuildToolbar()
    {
        _titleLabel.TextAlignment = UITextAlignment.Center;
        _subtitleLabel.TextAlignment = UITextAlignment.Center;
        var titleStack = WinoLayout.VStack(0, _titleLabel, _subtitleLabel);
        titleStack.Alignment = UIStackViewAlignment.Center;
        titleStack.IsAccessibilityElement = true;
        titleStack.AccessibilityTraits = UIAccessibilityTrait.Header;
        _titleView = titleStack;

        _syncButton = BarButton(WinoIconGlyph.Sync, Translator.Buttons_Sync, () => _ = SynchronizeAsync());
        _syncSpinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var syncHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_syncButton, syncHost);
        syncHost.AddSubview(_syncSpinner);
        _syncSpinner.CenterXAnchor.ConstraintEqualTo(syncHost.CenterXAnchor).Active = true;
        _syncSpinner.CenterYAnchor.ConstraintEqualTo(syncHost.CenterYAnchor).Active = true;
        _syncItem = new UIBarButtonItem(syncHost);

        var listItem = _listRoot.NavigationItem;
        listItem.LeftBarButtonItems =
        [
            new UIBarButtonItem(BarButton(WinoIconGlyph.PanelLeft, Translator.MacOSMenu_ToggleSidebar, ToggleSidebar)),
            _syncItem
        ];
        listItem.TitleView = _titleView;
        listItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;

        _search.Placeholder = Translator.SearchBarPlaceholder;
        _search.WidthAnchor.ConstraintLessThanOrEqualTo(420).Active = true;
        var preferredWidth = _search.WidthAnchor.ConstraintEqualTo(390);
        preferredWidth.Priority = 750;
        preferredWidth.Active = true;
        _searchDelegate = new SearchDelegate(this);
        _search.Delegate = _searchDelegate;

        _briefingButton = BarButton(WinoIconGlyph.DailyBriefing, Translator.DailyBriefing_Menu, ToggleBriefing);
        _briefingDot = new WinoSurfaceView { CornerRadius = 3, Fill = WinoStyle.Accent, Hidden = true, UserInteractionEnabled = false };
        WinoLayout.Size(_briefingDot, 6, 6);
        _briefingButton.AddSubview(_briefingDot);
        _briefingDot.TrailingAnchor.ConstraintEqualTo(_briefingButton.TrailingAnchor, -6).Active = true;
        _briefingDot.TopAnchor.ConstraintEqualTo(_briefingButton.TopAnchor, 6).Active = true;
        _briefingItem = new UIBarButtonItem(_briefingButton) { Hidden = true };
        _whatsNewItem = new UIBarButtonItem(BarButton(WinoIconGlyph.Announcement, Translator.WhatsNew_ShellButtonTooltip, ShowWhatsNew)) { Hidden = true };
        var avatar = new UIBarButtonItem(BarButton(WinoIconGlyph.Person, Translator.MenuSettings, OpenSettings));

        var readerItem = ReaderFrame.NavigationItem;
        readerItem.TitleView = _search;
        readerItem.RightBarButtonItems = [avatar, _whatsNewItem, _briefingItem];
        readerItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
    }

    /// <summary>A toolbar button drawing a Wino glyph (the Mac 17pt toolbar glyph raised for touch).</summary>
    private static UIButton BarButton(WinoIconGlyph glyph, string label, Action action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 20, null, label);
        configuration.ContentInsets = new NSDirectionalEdgeInsets(8, 8, 8, 8);
        var button = new UIButton { Configuration = configuration, PointerInteractionEnabled = true, TranslatesAutoresizingMaskIntoConstraints = false };
        button.PrimaryActionTriggered += (_, _) => action();
        WinoAccessibility.Label(button, label);
        button.LargeContentTitle = label;
        button.ShowsLargeContentViewer = true;
        WinoLayout.Size(button, 40, 40);
        return button;
    }

    private void BindToolbar()
    {
        Bind(nameof(ViewModel.CurrentProvider), vm => vm.CurrentProvider, ObserveProvider);
        _state.PropertyChanged += StateChanged;
        ObserveBriefing(Services.GetService<IUIKitDailyBriefingPresenter>());
        _ = RefreshWhatsNewAsync();
    }

    private void ReleaseToolbar()
    {
        _state.PropertyChanged -= StateChanged;
        ObserveProvider(null);
        ObserveTitle(null);
        ObserveBriefing(null);
        _search.WeakDelegate = null;
        _searchDelegate?.Dispose();
        _searchDelegate = null;
    }

    #region Title and subtitle

    private void ObserveTitle(INotifyPropertyChanged? source)
    {
        if (_titleSource is not null) _titleSource.PropertyChanged -= TitleSourceChanged;
        _titleSource = source;
        if (source is not null) source.PropertyChanged += TitleSourceChanged;
    }

    private async void TitleSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IBaseFolderMenuItem.UnreadItemCount) or nameof(IBaseFolderMenuItem.FolderName))) return;
        try { await Dispatcher.ExecuteOnUIThread(UpdateTitle); }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IStatePersistanceService.CoreWindowTitle) or nameof(IStatePersistanceService.AppModeTitle))) return;
        try { await Dispatcher.ExecuteOnUIThread(UpdateTitle); }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>In Mail the selected folder, then its account and unread count; in the other modes the mode title and what the page reports.</summary>
    private void UpdateTitle()
    {
        string title;
        string subtitle = string.Empty;
        if (ActiveMode is WinoApplicationMode.Mail or null && ViewModel.SelectedMenuItem is IBaseFolderMenuItem folder)
        {
            title = folder.FolderName ?? string.Empty;
            var owner = folder is IFolderMenuItem { ParentAccount: { } account }
                ? (string.IsNullOrWhiteSpace(account.Address) ? account.Name : account.Address)
                : folder.AssignedAccountName;
            var unread = folder.ShowUnreadCount && folder.UnreadItemCount > 0 ? string.Format(Translator.UnreadBadges_Account_UnreadFormat, folder.UnreadItemCount) : null;
            subtitle = string.Join(" · ", new[] { owner, unread }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        else
        {
            title = !string.IsNullOrWhiteSpace(_state.AppModeTitle) ? _state.AppModeTitle : ModeTitle(ActiveMode ?? WinoApplicationMode.Mail);
            subtitle = _state.CoreWindowTitle ?? string.Empty;
        }
        _titleLabel.Text = title;
        _subtitleLabel.Text = subtitle;
        _subtitleLabel.Hidden = string.IsNullOrWhiteSpace(subtitle);
        _titleView.AccessibilityLabel = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}, {subtitle}";
        _titleView.SizeToFit();
    }

    #endregion

    #region Synchronization

    private void ObserveProvider(IShellMenuProvider? provider)
    {
        if (_provider is not null) _provider.PropertyChanged -= ProviderChanged;
        _provider = provider;
        if (provider is not null) provider.PropertyChanged += ProviderChanged;
        ApplySynchronization();
    }

    private async void ProviderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(IShellMenuProvider.IsSynchronizationSupported) or nameof(IShellMenuProvider.CanSynchronize)
            or nameof(IShellMenuProvider.SynchronizationState) or nameof(IShellMenuProvider.SynchronizationDescription) or nameof(IShellMenuProvider.SynchronizationToolTip))) return;
        try { await Dispatcher.ExecuteOnUIThread(ApplySynchronization); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ApplySynchronization()
    {
        if (_syncItem is null) return;
        var provider = _provider;
        if (provider is null)
        {
            _syncItem.Hidden = true;
            return;
        }
        var state = provider.SynchronizationState;
        var tip = state.IsSynchronizing && !string.IsNullOrWhiteSpace(provider.SynchronizationDescription)
            ? provider.SynchronizationDescription
            : provider.SynchronizationToolTip;
        _syncItem.Hidden = !provider.IsSynchronizationSupported;
        _syncButton.Enabled = provider.CanSynchronize && !state.IsSynchronizing;
        _syncButton.Alpha = state.IsSynchronizing ? 0 : 1;
        if (state.IsSynchronizing) _syncSpinner.StartAnimating();
        else _syncSpinner.StopAnimating();
        var label = string.IsNullOrWhiteSpace(tip) ? Translator.Buttons_Sync : tip;
        WinoAccessibility.Label(_syncButton, label);
        _syncButton.LargeContentTitle = label;
    }

    #endregion

    #region Search

    public void FocusSearch()
    {
        if (!_search.UserInteractionEnabled) return;
        _split.ShowColumn(UISplitViewControllerColumn.Secondary);
        _search.BecomeFirstResponder();
    }

    /// <summary>Sets the field text without notifying the search target (keywords applied from the search filters).</summary>
    public void SetSearchText(string text)
    {
        _syncingSearchText = true;
        try { _search.Text = text ?? string.Empty; }
        finally { _syncingSearchText = false; }
    }

    /// <summary>Clears the field without notifying, for example after the content page changes.</summary>
    private void ResetSearch()
    {
        SetSearchText(string.Empty);
        _search.ResignFirstResponder();
    }

    /// <summary>Re-reads search support and the placeholder from the content page.</summary>
    private void UpdateSearchAvailability()
    {
        var target = SearchTarget;
        _search.UserInteractionEnabled = target is not null;
        _search.Alpha = target is null ? 0.5f : 1;
        _search.Placeholder = target?.SearchPlaceholder ?? Translator.SearchBarPlaceholder;
    }

    private async void SearchTextChanged(string text)
    {
        if (_syncingSearchText) return;
        try
        {
            if (SearchTarget is not { } target) return;
            (target as IShellSearchSuggestionTarget)?.SearchFieldEditing(_search.SearchTextField);
            await target.SearchTextChangedAsync(text);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void SearchSubmitted()
    {
        try
        {
            if (SearchTarget is not { } target) return;
            if (target is IShellSearchSuggestionTarget suggestions && suggestions.SearchFieldCommand(_search.SearchTextField, ShellSearchFieldCommand.Submit)) return;
            var text = _search.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) await target.SearchClearedAsync();
            else await target.SearchSubmittedAsync(text);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private void SearchEnded()
    {
        try { (SearchTarget as IShellSearchSuggestionTarget)?.SearchFieldEndedEditing(_search.SearchTextField); }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>True while the search field has the keyboard focus.</summary>
    internal bool IsSearchEditing => _search.SearchTextField.IsFirstResponder;

    internal void EndSearchEditing() => _search.ResignFirstResponder();

    /// <summary>↑, ↓ and Esc while the field edits go to a suggestion target first.</summary>
    internal bool SearchFieldCommand(ShellSearchFieldCommand command)
        => _search.SearchTextField.IsFirstResponder && SearchTarget is IShellSearchSuggestionTarget suggestions
            && suggestions.SearchFieldCommand(_search.SearchTextField, command);

    private sealed class SearchDelegate(PadShellViewController owner) : UISearchBarDelegate
    {
        public override void TextChanged(UISearchBar searchBar, string searchText) => owner.SearchTextChanged(searchText ?? string.Empty);

        public override void SearchButtonClicked(UISearchBar searchBar) => owner.SearchSubmitted();

        public override void OnEditingStopped(UISearchBar searchBar) => owner.SearchEnded();
    }

    #endregion

    #region Daily briefing, What's New

    private void BuildBriefingPanel(UIView root)
    {
        _briefingHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true, ClipsToBounds = false };
        _briefingHost.Layer.ShadowColor = UIColor.Black.CGColor;
        _briefingHost.Layer.ShadowOpacity = 0.18f;
        _briefingHost.Layer.ShadowRadius = 16;
        root.AddSubview(_briefingHost);
        var width = _briefingHost.WidthAnchor.ConstraintEqualTo((nfloat)BriefingPanelWidth);
        width.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            _briefingTop = _briefingHost.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor, 50),
            _briefingHost.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _briefingHost.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            _briefingHost.WidthAnchor.ConstraintLessThanOrEqualTo(root.WidthAnchor),
            width
        ]);
    }

    /// <summary>Keeps the panel below the toolbar row (the reader column's navigation bar).</summary>
    private void LayoutBriefingPanel()
    {
        if (_briefingTop is { } top && _reader.NavigationBar.Bounds.Height is var height && height > 0 && top.Constant != height) top.Constant = height;
    }

    private void ObserveBriefing(IUIKitDailyBriefingPresenter? presenter)
    {
        if (_briefing is not null) _briefing.UnseenItemsChanged -= BriefingChanged;
        _briefing = presenter;
        if (presenter is not null) presenter.UnseenItemsChanged += BriefingChanged;
        ApplyBriefing();
    }

    private async void BriefingChanged(object? sender, EventArgs args)
    {
        try { await Dispatcher.ExecuteOnUIThread(ApplyBriefing); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ApplyBriefing()
    {
        if (_briefingItem is null) return;
        _briefingItem.Hidden = _briefing is not { IsAvailable: true };
        _briefingDot.Hidden = _briefing?.HasUnseenItems != true;
        _briefingDot.Fill = WinoStyle.Accent;
        _briefingButton.Selected = _briefingOpen;
        _briefingButton.AccessibilityValue = _briefing?.HasUnseenItems == true ? Translator.DailyBriefing_Title : null;
    }

    /// <summary>Shows or hides the briefing panel as a right overlay, 400pt wide, below the toolbar row.</summary>
    private void ToggleBriefing()
    {
        if (_briefing is null || _briefingHost is null) return;
        if (_briefingPanel is null)
        {
            _briefingPanel = _briefing.CreatePanel(() => SetBriefingOpen(false));
            AddChildViewController(_briefingPanel);
            if (_briefingPanel.View is { } panel) WinoLayout.Fill(panel, _briefingHost);
            _briefingPanel.DidMoveToParentViewController(this);
        }
        SetBriefingOpen(!_briefingOpen);
    }

    private void SetBriefingOpen(bool open)
    {
        _briefingOpen = open;
        if (_briefingHost is { } host)
        {
            host.Hidden = !open;
            if (open) View?.BringSubviewToFront(host);
        }
        ApplyBriefing();
    }

    private async Task RefreshWhatsNewAsync()
    {
        try
        {
            var service = Services.GetService<IWhatsNewService>();
            var presenter = Services.GetService<IUIKitWhatsNewPresenter>();
            bool show = service is not null && presenter is not null && await service.ShouldShowShellEntryAsync();
            await Dispatcher.ExecuteOnUIThread(() => _whatsNewItem.Hidden = !show);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void ShowWhatsNew()
    {
        try
        {
            if (Services.GetService<IUIKitWhatsNewPresenter>() is not { } presenter) return;
            await presenter.ShowAsync();
            Services.GetService<IWhatsNewService>()?.MarkOpenedForCurrentVersion();
            _whatsNewItem.Hidden = true;
        }
        catch (Exception exception) { ReportError(exception); }
    }

    #endregion
}
