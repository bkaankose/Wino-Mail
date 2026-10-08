using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// The Daily briefing panel (Windows DailyBriefingPanel): 400pt wide, solid secondary background,
/// top-left radius 8, stroke and shadow; a 48pt header, the 40pt day strip and the card list or one
/// of the loading, empty, filtered, unavailable and error states. The panel loads when its view
/// becomes visible and marks the briefing viewed when it is hidden or removed, so the shell can
/// either add/remove it or toggle <see cref="NSView.Hidden"/>.
/// </summary>
public sealed class DailyBriefingPanelViewController : NSViewController
{
    public const double PanelWidth = 400;

    /// <summary>Windows SolidBackgroundFillColorSecondary.</summary>
    private static NSColor PanelFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF6F6F7), WinoStyle.Hex(0x2B2B2E));

    private readonly DailyBriefingPanelViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly Action _close;
    private readonly Dictionary<DailyBriefingItem, (WinoBriefingCardView Card, PropertyChangedEventHandler Handler)> _cards = new();

    private NSButton _showIgnored = null!;
    private WinoDatePager _pager = null!;
    private NSStackView _list = null!;
    private NSScrollView _scroll = null!;
    private WinoStateView _loading = null!;
    private WinoStateView _empty = null!;
    private WinoStateView _filtered = null!;
    private WinoStateView _unavailable = null!;
    private WinoStateView _error = null!;
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
        _viewModel.CloseRequested += (_, _) => _ = Ui(_close);
    }

    public DailyBriefingPanelViewModel ViewModel => _viewModel;

    public override void LoadView()
    {
        // The shadow lives on the root; the clipped surface inside it draws the rounded corner and stroke.
        var root = new PanelRootView(this);
        root.WidthAnchor.ConstraintEqualTo((nfloat)PanelWidth).Active = true;
        WinoAccessibility.Label(root, Translator.DailyBriefing_Title);
        var surface = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.ZoneRadius,
            Corners = CACornerMask.MinXMinYCorner,
            Fill = PanelFill,
            Stroke = WinoStyle.ZoneStroke
        };
        WinoLayout.Fill(surface, root);

        // Header: glyph, title, show-ignored toggle, close (48pt, padding 16/8).
        var icon = _headerIcon = new WinoIconView(WinoIconGlyph.DailyBriefing, 18, WinoStyle.Accent);
        var title = WinoStyle.Label(Translator.DailyBriefing_Title, WinoStyle.BodyStrong);
        title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _showIgnored = IconButton(WinoIconGlyph.Eye, 15, Translator.DailyBriefing_ShowIgnored);
        _showIgnored.SetButtonType(NSButtonType.PushOnPushOff);
        _showIgnored.Bordered = true;
        _showIgnored.BezelStyle = NSBezelStyle.Recessed;
        _showIgnored.ShowsBorderOnlyWhileMouseInside = true;
        _showIgnored.Activated += (_, _) => _viewModel.IsShowingIgnored = _showIgnored.State == NSCellStateValue.On;
        var close = IconButton(WinoIconGlyph.Dismiss, 14, Translator.DailyBriefing_ClosePane);
        close.Activated += (_, _) => _close();
        var header = WinoLayout.HStack(4, icon, title, _showIgnored, close);
        header.SetCustomSpacing(8, icon);
        header.EdgeInsets = new NSEdgeInsets(0, 16, 0, 8);
        header.HeightAnchor.ConstraintEqualTo(48).Active = true;

        _pager = new WinoDatePager(Translator.DailyBriefing_NextDay, Translator.DailyBriefing_PreviousDay);
        _pager.SelectedIndexChanged += (_, _) => _viewModel.SelectedDateIndex = _pager.SelectedIndex;

        // Content: card list in a scroll view, state views over it.
        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _list = WinoLayout.VStack(6);
        _list.EdgeInsets = new NSEdgeInsets(6, 4, 6, 4);
        var document = new FlippedView();
        document.AddSubview(_list);
        _scroll = new NSScrollView
        {
            DocumentView = document,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        NSLayoutConstraint.ActivateConstraints(
        [
            _list.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _list.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _list.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            document.WidthAnchor.ConstraintEqualTo(_scroll.ContentView.WidthAnchor),
            document.HeightAnchor.ConstraintGreaterThanOrEqualTo(_list.HeightAnchor)
        ]);
        WinoLayout.Fill(_scroll, content);

        _loading = new WinoStateView(WinoIconGlyph.Info, string.Empty, Translator.DailyBriefing_ReadingLocal);
        _loading.Title.Hidden = true;
        _loading.UseSpinner().StartAnimation(null);
        _empty = new WinoStateView(WinoIconGlyph.Info, Translator.DailyBriefing_EmptyTitle, Translator.DailyBriefing_EmptyMessage);
        _filtered = new WinoStateView(WinoIconGlyph.EyeOff, Translator.DailyBriefing_FilteredEmptyTitle, Translator.DailyBriefing_FilteredEmptyMessage);
        _unavailable = new WinoStateView(WinoIconGlyph.Info, Translator.DailyBriefing_UnavailableTitle, Translator.DailyBriefing_UnavailableMessage);
        _error = new WinoStateView(WinoIconGlyph.AlertCircle, Translator.DailyBriefing_LocalErrorTitle, string.Empty, WinoStyle.Critical);
        var retry = new NSButton { Title = Translator.DailyBriefing_Retry, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        retry.Activated += (_, _) => _viewModel.RetryCommand.Execute(null);
        _error.Add(retry);
        foreach (var state in new[] { _loading, _empty, _filtered, _unavailable, _error }) state.Place(content);

        var column = WinoLayout.VStack(0, header, _pager, content);
        foreach (var row in new NSView[] { header, _pager, content })
        {
            row.LeadingAnchor.ConstraintEqualTo(column.LeadingAnchor).Active = true;
            row.TrailingAnchor.ConstraintEqualTo(column.TrailingAnchor).Active = true;
        }
        content.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(column, surface, 1, 1, 0, 0);
        View = root;

        WinoStyle.AccentChanged += AccentChanged;
        RefreshDates();
        RefreshItems();
        RefreshState();
        _showIgnored.State = _viewModel.IsShowingIgnored ? NSCellStateValue.On : NSCellStateValue.Off;
    }

    private WinoIconView? _headerIcon;

    private void AccentChanged(object? sender, EventArgs args)
    {
        if (_headerIcon is not null) _headerIcon.Tint = WinoStyle.Accent;
    }

    private static NSButton IconButton(WinoIconGlyph glyph, double size, string tooltip)
    {
        var button = new NSButton
        {
            Bordered = false,
            Image = WinoIcons.Image(glyph, size),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ToolTip = tooltip,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(button, tooltip);
        WinoLayout.Size(button, 34, 32);
        return button;
    }

    /// <summary>Called by the root view when it enters or leaves the visible hierarchy.</summary>
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

    private Task Ui(Action action) => _dispatcher.ExecuteOnUIThread(() => { if (!_disposed) action(); });

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) => _ = Ui(() =>
    {
        switch (args.PropertyName)
        {
            case nameof(DailyBriefingPanelViewModel.SelectedDateIndex):
                _pager.Select(_viewModel.SelectedDateIndex);
                break;
            case nameof(DailyBriefingPanelViewModel.IsShowingIgnored):
                _showIgnored.State = _viewModel.IsShowingIgnored ? NSCellStateValue.On : NSCellStateValue.Off;
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
        _error.Caption.StringValue = vm.LoadError;
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
            card.RemoveFromSuperview();
        }
        _cards.Clear();
        foreach (var item in _viewModel.Items)
        {
            var card = new WinoBriefingCardView();
            card.Update(Describe(item));
            card.PrimaryActivated += (_, _) => _viewModel.ExecutePrimaryActionCommand.Execute(item);
            card.IgnoreActivated += (_, _) => _viewModel.IgnoreCommand.Execute(item);
            PropertyChangedEventHandler handler = (_, _) => _ = Ui(() => card.Update(Describe(item)));
            item.PropertyChanged += handler;
            _cards[item] = (card, handler);
            _list.AddArrangedSubview(card);
            card.LeadingAnchor.ConstraintEqualTo(_list.LeadingAnchor, 4).Active = true;
            card.TrailingAnchor.ConstraintEqualTo(_list.TrailingAnchor, -4).Active = true;
        }
    }

    /// <summary>Mirrors the Windows DailyBriefingPanelPresentation helpers.</summary>
    private static WinoBriefingCardData Describe(DailyBriefingItem item)
    {
        var fact = item.Fact;
        var headline = string.IsNullOrWhiteSpace(fact.Headline) ? fact.Subject : fact.Headline;
        var time = TimeZoneInfo.ConvertTime(fact.ReceivedAt, TimeZoneInfo.Local).ToString("t", CultureInfo.CurrentCulture);
        string? urgency = item.IsPriority
            ? string.Equals(fact.Priority, "urgent", StringComparison.OrdinalIgnoreCase) ? Translator.DailyBriefing_UrgencyUrgent : Translator.DailyBriefing_UrgencyHigh
            : null;
        // Two chips fit beside the longest action wording; a priority chip takes one of the places.
        var room = urgency is null ? 2 : 1;
        var labels = item.LabelChips.Take(room).Select(chip => chip.Text).ToList();
        return new WinoBriefingCardData(headline, time, item.IsNew, item.HasSummary ? item.Summary : null, urgency, labels,
            item.PrimaryActionGlyph, item.PrimaryActionText, item.IgnoreActionGlyph, item.IgnoreActionText, item.CanToggleIgnore);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            WinoStyle.AccentChanged -= AccentChanged;
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            _viewModel.Items.CollectionChanged -= ItemsChanged;
            _viewModel.Dates.CollectionChanged -= DatesChanged;
            foreach (var (item, (_, handler)) in _cards) item.PropertyChanged -= handler;
            _cards.Clear();
        }
        base.Dispose(disposing);
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }

    /// <summary>Reports window attachment and hidden changes so the controller knows when it is on screen.</summary>
    private sealed class PanelRootView : NSView
    {
        private readonly DailyBriefingPanelViewController _owner;

        public PanelRootView(DailyBriefingPanelViewController owner)
        {
            _owner = owner;
            TranslatesAutoresizingMaskIntoConstraints = false;
            WantsLayer = true;
            Shadow = new NSShadow { ShadowBlurRadius = 24, ShadowOffset = new CGSize(-6, 0), ShadowColor = NSColor.Black.ColorWithAlphaComponent(0.18f) };
        }

        private void Report() => _owner.VisibilityChanged(Window is not null && !IsHiddenOrHasHiddenAncestor);
        public override void ViewDidMoveToWindow() { base.ViewDidMoveToWindow(); Report(); }
        public override void ViewDidHide() { base.ViewDidHide(); Report(); }
        public override void ViewDidUnhide() { base.ViewDidUnhide(); Report(); }
    }
}
