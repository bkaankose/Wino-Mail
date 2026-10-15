using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.ViewModels;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Extras;

/// <summary>
/// The Daily briefing panel (Mac DailyBriefingPanelViewController, Windows DailyBriefingPanel): the header (glyph, title,
/// show-ignored toggle, close), the day pager, the card list or one of the loading, empty, filtered, unavailable and
/// error states, and the privacy line (the briefing is built from facts stored on this device). One controller for both
/// idioms: an overlay panel on iPad, a sheet on iPhone. It loads when it appears and marks the briefing viewed when it
/// disappears or its view is hidden, so the shell can add/remove it, present/dismiss it or toggle Hidden.
/// </summary>
public sealed class DailyBriefingPanelViewController : UIViewController
{
    /// <summary>The iPad overlay width (Windows 400).</summary>
    public const double PanelWidth = 400;

    /// <summary>Windows SolidBackgroundFillColorSecondary.</summary>
    private static UIColor PanelFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF6F6F7), WinoStyle.Hex(0x2B2B2E));

    private readonly DailyBriefingPanelViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly Action _close;
    private readonly Dictionary<DailyBriefingItem, (WinoBriefingCardView Card, PropertyChangedEventHandler Handler)> _cards = new();
    private readonly UIButton _showIgnored = IconButton(WinoIconGlyph.Eye, Translator.DailyBriefing_ShowIgnored);
    private readonly WinoDatePager _pager = new(Translator.DailyBriefing_NextDay, Translator.DailyBriefing_PreviousDay);
    private readonly UIStackView _list = WinoLayout.VStack(8);
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
    private readonly WinoStateView _loading = new(WinoIconGlyph.Info, string.Empty, Translator.DailyBriefing_ReadingLocal);
    private readonly WinoStateView _empty = new(WinoIconGlyph.Info, Translator.DailyBriefing_EmptyTitle, Translator.DailyBriefing_EmptyMessage);
    private readonly WinoStateView _filtered = new(WinoIconGlyph.EyeOff, Translator.DailyBriefing_FilteredEmptyTitle, Translator.DailyBriefing_FilteredEmptyMessage);
    private readonly WinoStateView _unavailable = new(WinoIconGlyph.Info, Translator.DailyBriefing_UnavailableTitle, Translator.DailyBriefing_UnavailableMessage);
    private readonly WinoStateView _error = new(WinoIconGlyph.AlertCircle, Translator.DailyBriefing_LocalErrorTitle, string.Empty, WinoStyle.Critical);
    private WinoIconView? _headerIcon;
    private bool _visible;
    private bool _disposed;

    public DailyBriefingPanelViewController(DailyBriefingPanelViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, Action close)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _logger = logger;
        _close = close;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        _viewModel.Items.CollectionChanged += ItemsChanged;
        _viewModel.Dates.CollectionChanged += DatesChanged;
        _viewModel.CloseRequested += ViewModelCloseRequested;
    }

    public DailyBriefingPanelViewModel ViewModel => _viewModel;

    public override void LoadView()
    {
        var root = new PanelRootView(this) { BackgroundColor = PanelFill };
        WinoAccessibility.Label(root, Translator.DailyBriefing_Title);

        // Header: glyph, title, show-ignored toggle, close (52pt, padding 16/8).
        var icon = _headerIcon = new WinoIconView(WinoIconGlyph.DailyBriefing, 20, WinoStyle.Accent);
        WinoLayout.Size(icon, 20, 20);
        var title = WinoStyle.Label(Translator.DailyBriefing_Title, WinoStyle.Scaled(17, UIFontWeight.Semibold));
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        title.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _showIgnored.ChangesSelectionAsPrimaryAction = true;
        _showIgnored.PrimaryActionTriggered += (_, _) => _viewModel.IsShowingIgnored = _showIgnored.Selected;
        var close = IconButton(WinoIconGlyph.Dismiss, Translator.DailyBriefing_ClosePane);
        close.PrimaryActionTriggered += (_, _) => _close();
        var header = WinoLayout.Insets(WinoLayout.HStack(4, icon, title, _showIgnored, close), 0, 16, 0, 8);
        header.SetCustomSpacing(10, icon);
        header.HeightAnchor.ConstraintEqualTo(52).Active = true;

        _pager.SelectedIndexChanged += (_, _) => _viewModel.SelectedDateIndex = _pager.SelectedIndex;

        // Content: card list in a scroll view, state views over it.
        var content = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        _list.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Insets(_list, 8, 12, 12, 12);
        _scroll.AddSubview(_list);
        NSLayoutConstraint.ActivateConstraints(
        [
            _list.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor),
            _list.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor),
            _list.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            _list.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            _list.WidthAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.WidthAnchor)
        ]);
        WinoLayout.Fill(_scroll, content);

        _loading.Title.Hidden = true;
        _loading.UseSpinner();
        var retryConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        retryConfiguration.Title = Translator.DailyBriefing_Retry;
        var retry = new UIButton(UIButtonType.System) { Configuration = retryConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        retry.PrimaryActionTriggered += (_, _) => _viewModel.RetryCommand.Execute(null);
        _error.Add(retry);
        foreach (var state in new[] { _loading, _empty, _filtered, _unavailable, _error }) state.Place(content);

        // Privacy line: the briefing reads the local intelligence store only.
        var privacy = WinoStyle.Label(Translator.DailyBriefing_LocalHint, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        privacy.TextAlignment = UITextAlignment.Center;
        var privacyRow = WinoLayout.Insets(WinoLayout.VStack(0, privacy), 8, 16, 12, 16);
        privacyRow.Alignment = UIStackViewAlignment.Fill;
        var rule = new WinoSeparator { Fill = WinoBriefingCardView.CardStroke };

        var column = WinoLayout.VStack(0, header, _pager, content, rule, privacyRow);
        column.Alignment = UIStackViewAlignment.Fill;
        content.SetContentHuggingPriority(1, UILayoutConstraintAxis.Vertical);
        content.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Vertical);
        root.AddSubview(column);
        NSLayoutConstraint.ActivateConstraints(
        [
            column.LeadingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TrailingAnchor),
            column.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            column.BottomAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.BottomAnchor)
        ]);
        View = root;

        WinoStyle.AccentChanged += AccentChanged;
        RefreshDates();
        RefreshItems();
        RefreshState();
        _showIgnored.Selected = _viewModel.IsShowingIgnored;
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        VisibilityChanged(View?.Hidden != true);
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        VisibilityChanged(false);
    }

    private void AccentChanged(object? sender, EventArgs args)
    {
        if (_headerIcon is not null) _headerIcon.Tint = WinoStyle.Accent;
    }

    private static UIButton IconButton(WinoIconGlyph glyph, string tooltip)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 18, null, tooltip);
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.ConfigurationUpdateHandler = static updated =>
        {
            if (updated.Configuration is not { } current) return;
            current.Background.BackgroundColor = updated.Selected ? WinoStyle.SelectedFill : UIColor.Clear;
            current.Background.CornerRadius = 8;
            updated.Configuration = current;
        };
        WinoAccessibility.Label(button, tooltip);
        WinoLayout.Size(button, 44, 44);
        return button;
    }

    /// <summary>Called when the panel appears, disappears, or its root view is hidden or shown.</summary>
    private void VisibilityChanged(bool visible)
    {
        if (_disposed || visible == _visible) return;
        _visible = visible;
        _ = visible ? Observe(_viewModel.InitializeAsync()) : Observe(_viewModel.MarkViewedAsync());
    }

    private async Task Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(DailyBriefingPanelViewController)); }
    }

    private Task Ui(Action action) => _dispatcher.ExecuteOnUIThread(() => { if (!_disposed && IsViewLoaded) action(); });

    private void ViewModelCloseRequested(object? sender, EventArgs args) => _ = _dispatcher.ExecuteOnUIThread(() => { if (!_disposed) _close(); });

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) => _ = Ui(() =>
    {
        switch (args.PropertyName)
        {
            case nameof(DailyBriefingPanelViewModel.SelectedDateIndex):
                _pager.Select(_viewModel.SelectedDateIndex);
                break;
            case nameof(DailyBriefingPanelViewModel.IsShowingIgnored):
                _showIgnored.Selected = _viewModel.IsShowingIgnored;
                break;
            case nameof(DailyBriefingPanelViewModel.NewItemCount):
            case nameof(DailyBriefingPanelViewModel.HasIgnoredItems):
                break;
            default:
                RefreshState();
                break;
        }
    });

    private void DatesChanged(object? sender, NotifyCollectionChangedEventArgs args) => _ = Ui(RefreshDates);

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => _ = Ui(RefreshItems);

    private void RefreshDates()
    {
        _pager.SetItems(_viewModel.Dates.Select(date => date.DisplayName).ToList());
        _pager.Select(_viewModel.SelectedDateIndex);
    }

    private void RefreshState()
    {
        var vm = _viewModel;
        _loading.Hidden = !vm.IsLoading;
        _unavailable.Hidden = !(vm.IsUnavailable && !vm.IsLoading);
        _error.Hidden = !(vm.HasLoadError && !vm.IsLoading && !vm.IsUnavailable);
        _error.Caption.Text = vm.LoadError;
        _error.Caption.Hidden = !vm.HasLoadError;
        _empty.Hidden = !(vm.IsEmpty && !vm.IsLoading && !vm.IsUnavailable && !vm.HasLoadError);
        _filtered.Hidden = !vm.ShowFilteredEmpty;
        _scroll.Hidden = !vm.ShowContent;
    }

    /// <summary>Rebuilds the card list from the ViewModel's items; cards track their item's ignore and new state.</summary>
    private void RefreshItems()
    {
        foreach (var (item, (card, handler)) in _cards)
        {
            item.PropertyChanged -= handler;
            _list.RemoveArrangedSubview(card);
            card.RemoveFromSuperview();
        }
        _cards.Clear();
        foreach (var item in _viewModel.Items)
        {
            var card = new WinoBriefingCardView();
            card.Update(Describe(item));
            var captured = item;
            card.PrimaryActivated += (_, _) => _viewModel.ExecutePrimaryActionCommand.Execute(captured);
            card.IgnoreActivated += (_, _) => _viewModel.IgnoreCommand.Execute(captured);
            PropertyChangedEventHandler handler = (_, _) => _ = Ui(() => card.Update(Describe(captured)));
            item.PropertyChanged += handler;
            _cards[item] = (card, handler);
            _list.AddArrangedSubview(card);
        }
    }

    /// <summary>Mirrors the Windows DailyBriefingPanelPresentation helpers (Mac Describe).</summary>
    private static WinoBriefingCardData Describe(DailyBriefingItem item)
    {
        var card = DailyBriefingCardContent.From(item);
        return new WinoBriefingCardData(card.Headline, card.Time, card.IsNew, card.Summary, card.Urgency, card.Labels,
            card.ActionGlyph, card.ActionText, card.IgnoreGlyph, card.IgnoreText, card.CanToggleIgnore);
    }

#if DEBUG
    internal string Dump()
        => $"visible={_visible} cards={_cards.Count} dates={_viewModel.Dates.Count} loading={!_loading.Hidden} empty={!_empty.Hidden} " +
           $"unavailable={!_unavailable.Hidden} error={!_error.Hidden} content={!_scroll.Hidden} size={View?.Bounds.Size}";
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            WinoStyle.AccentChanged -= AccentChanged;
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            _viewModel.Items.CollectionChanged -= ItemsChanged;
            _viewModel.Dates.CollectionChanged -= DatesChanged;
            _viewModel.CloseRequested -= ViewModelCloseRequested;
            foreach (var (item, (_, handler)) in _cards) item.PropertyChanged -= handler;
            _cards.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>Reports Hidden changes so an overlay the shell toggles still loads and marks the briefing viewed.</summary>
    private sealed class PanelRootView(DailyBriefingPanelViewController owner) : UIView
    {
        public override bool Hidden
        {
            get => base.Hidden;
            set
            {
                base.Hidden = value;
                if (Window is not null) owner.VisibilityChanged(!value);
            }
        }
    }
}
