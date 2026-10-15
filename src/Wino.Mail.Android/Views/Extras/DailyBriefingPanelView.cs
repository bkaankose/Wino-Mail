using System.Collections.Specialized;
using System.ComponentModel;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Extras;

/// <summary>
/// The Daily briefing panel (Windows DailyBriefingPanel, iOS and Mac DailyBriefingPanelViewController) in the Material 3
/// look of the canvas Briefing board: the drag handle (bottom sheet only), a 64 dp header with the briefing glyph, the
/// title, the show-ignored toggle and Close, the day pager, the cards or one of the loading, empty, filtered,
/// unavailable and error states, and the privacy line. Content is exactly what <see cref="DailyBriefingPanelViewModel"/>
/// provides; each card shows <see cref="DailyBriefingCardContent"/>. It loads when attached and marks the briefing viewed
/// when detached.
/// </summary>
public sealed class DailyBriefingPanelView : LinearLayout
{
    private readonly DailyBriefingPanelViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly Dictionary<DailyBriefingItem, (BriefingCard Card, PropertyChangedEventHandler Handler)> _cards = new();
    private readonly LinearLayout _list;
    private readonly NestedScrollView _scroll;
    private readonly ImageButton _showIgnored;
    private readonly ImageButton _newer;
    private readonly ImageButton _older;
    private readonly TextView _day;
    private readonly View _loading;
    private readonly View _empty;
    private readonly View _filtered;
    private readonly View _unavailable;
    private readonly View _error;
    private readonly TextView _errorMessage;
    private bool _attached;

    public DailyBriefingPanelView(Context context, DailyBriefingPanelViewModel viewModel, IDispatcher dispatcher, bool showHandle) : base(context)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        Orientation = global::Android.Widget.Orientation.Vertical;
        ContentDescription = Translator.DailyBriefing_Title;

        if (showHandle) AddView(new BottomSheetDragHandleView(context), new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Header: glyph, title, show-ignored toggle, close (64 dp).
        var header = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_header_height));
        WinoTokens.Pad(header, Resource.Dimension.wino_m3_briefing_header_padding_start, 0, Resource.Dimension.wino_m3_briefing_header_padding_end, 0);
        var glyph = new WinoIconView(context) { Icon = WinoIconGlyph.DailyBriefing, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_briefing_header_glyph), Gravity = GravityFlags.Center };
        glyph.SetTextColor(WinoColors.Accent(context));
        header.AddView(glyph, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_header_gap) });
        var title = WinoTokens.Style(new TextView(context) { Text = Translator.DailyBriefing_Title }, WinoTextStyle.TopAppBarTitle);
        title.SetMaxLines(1);
        title.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(title, true);
        header.AddView(title, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _showIgnored = IconButton(context, WinoIcons.Glyph(WinoIconGlyph.Eye), Translator.DailyBriefing_ShowIgnored);
        _showIgnored.Click += (_, _) => _viewModel.IsShowingIgnored = !_viewModel.IsShowingIgnored;
        header.AddView(_showIgnored);
        var close = IconButton(context, WinoIcons.Glyph(WinoIconGlyph.Dismiss), Translator.DailyBriefing_ClosePane);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        header.AddView(close);
        AddView(header, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Day pager: newer (left), the day, older (right); index 0 is today.
        var pager = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
        pager.SetGravity(GravityFlags.CenterVertical);
        pager.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_pager_height));
        WinoTokens.Pad(pager, Resource.Dimension.wino_space_xs, 0, Resource.Dimension.wino_space_xs, 0);
        _newer = IconButton(context, WinoIcons.Glyph(WinoIconGlyph.ArrowLeft), Translator.DailyBriefing_NextDay);
        _newer.Click += (_, _) => Page(-1);
        _older = IconButton(context, WinoIcons.Glyph(WinoIconGlyph.ArrowRight), Translator.DailyBriefing_PreviousDay);
        _older.Click += (_, _) => Page(1);
        _day = WinoTokens.Style(new TextView(context) { Gravity = GravityFlags.Center }, WinoTextStyle.BodyStrong);
        _day.SetTextColor(WinoColors.PrimaryText(context));
        _day.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        pager.AddView(_newer);
        pager.AddView(_day, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        pager.AddView(_older);
        AddView(pager, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        AddView(Divider(context));

        // Content: the cards, with the state views over them.
        var content = new FrameLayout(context);
        _list = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical };
        WinoTokens.Pad(_list, Resource.Dimension.wino_m3_briefing_list_padding_horizontal, Resource.Dimension.wino_m3_briefing_list_padding_vertical,
            Resource.Dimension.wino_m3_briefing_list_padding_horizontal, Resource.Dimension.wino_m3_briefing_list_padding_vertical);
        _scroll = new NestedScrollView(context) { FillViewport = true };
        _scroll.AddView(_list, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        content.AddView(_scroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var spinner = new CircularProgressIndicator(context) { Indeterminate = true };
        spinner.SetIndicatorColor(WinoColors.Accent(context));
        _loading = State(context, content, spinner, null, Translator.DailyBriefing_ReadingLocal);
        _empty = State(context, content, StateGlyph(context, WinoIconGlyph.Info, WinoColors.SecondaryText(context)), Translator.DailyBriefing_EmptyTitle, Translator.DailyBriefing_EmptyMessage);
        _filtered = State(context, content, StateGlyph(context, WinoIconGlyph.EyeOff, WinoColors.SecondaryText(context)), Translator.DailyBriefing_FilteredEmptyTitle, Translator.DailyBriefing_FilteredEmptyMessage);
        _unavailable = State(context, content, StateGlyph(context, WinoIconGlyph.Info, WinoColors.SecondaryText(context)), Translator.DailyBriefing_UnavailableTitle, Translator.DailyBriefing_UnavailableMessage);
        _error = State(context, content, StateGlyph(context, WinoIconGlyph.AlertCircle, WinoColors.Critical(context)), Translator.DailyBriefing_LocalErrorTitle, string.Empty, out _errorMessage);
        var retry = new MaterialButton(context, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = Translator.DailyBriefing_Retry };
        retry.SetAllCaps(false);
        retry.Click += (_, _) => _viewModel.RetryCommand.Execute(null);
        ((LinearLayout)_error).AddView(retry, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_m) });
        AddView(content, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        // Privacy line: the briefing reads the local intelligence store only.
        AddView(Divider(context));
        var privacy = WinoTokens.Style(new TextView(context) { Text = Translator.DailyBriefing_LocalHint, Gravity = GravityFlags.Center }, WinoTextStyle.Caption);
        privacy.SetTextColor(WinoColors.SecondaryText(context));
        WinoTokens.Pad(privacy, Resource.Dimension.wino_m3_briefing_footer_padding_horizontal, Resource.Dimension.wino_m3_briefing_footer_padding_vertical,
            Resource.Dimension.wino_m3_briefing_footer_padding_horizontal, Resource.Dimension.wino_m3_briefing_footer_padding_vertical);
        AddView(privacy, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    /// <summary>Close was tapped, or the ViewModel asked to close (opening a message).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Starts observing the ViewModel and loads the briefing (the panel was shown).</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        _viewModel.Items.CollectionChanged += ItemsChanged;
        _viewModel.Dates.CollectionChanged += DatesChanged;
        _viewModel.CloseRequested += ViewModelCloseRequested;
        RefreshDates();
        RefreshItems();
        RefreshState();
        _ = Observe(_viewModel.InitializeAsync());
    }

    /// <summary>Stops observing and marks the briefing viewed (the panel was dismissed).</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel.Items.CollectionChanged -= ItemsChanged;
        _viewModel.Dates.CollectionChanged -= DatesChanged;
        _viewModel.CloseRequested -= ViewModelCloseRequested;
        foreach (var (item, (_, handler)) in _cards) item.PropertyChanged -= handler;
        _cards.Clear();
        _ = Observe(_viewModel.MarkViewedAsync());
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "The Daily briefing panel failed."); }
    }

    private Task Ui(Action action) => _dispatcher.ExecuteOnUIThread(() => { if (_attached) action(); });

    private void ViewModelCloseRequested(object? sender, EventArgs args) => _ = Ui(() => CloseRequested?.Invoke(this, EventArgs.Empty));

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) => _ = Ui(() =>
    {
        switch (args.PropertyName)
        {
            case nameof(DailyBriefingPanelViewModel.SelectedDateIndex):
                RefreshDay();
                break;
            case nameof(DailyBriefingPanelViewModel.IsShowingIgnored):
                RefreshIgnoredToggle();
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

    private void Page(int step)
    {
        int index = _viewModel.SelectedDateIndex + step;
        if (index >= 0 && index < _viewModel.Dates.Count) _viewModel.SelectedDateIndex = index;
    }

    private void RefreshDates()
    {
        RefreshDay();
        RefreshIgnoredToggle();
    }

    private void RefreshDay()
    {
        int index = _viewModel.SelectedDateIndex;
        _day.Text = index >= 0 && index < _viewModel.Dates.Count ? _viewModel.Dates[index].DisplayName : string.Empty;
        SetEnabled(_newer, index > 0);
        SetEnabled(_older, index >= 0 && index < _viewModel.Dates.Count - 1);
    }

    private void RefreshIgnoredToggle()
    {
        var context = this.RequireContext();
        bool on = _viewModel.IsShowingIgnored;
        _showIgnored.Selected = on;
        _showIgnored.BackgroundTintList = ColorStateList.ValueOf(on ? WinoColors.SecondaryContainer(context) : Color.Transparent);
        AndroidX.Core.View.ViewCompat.SetStateDescription(_showIgnored, on ? Translator.DailyBriefing_ActionUnignore : null);
    }

    private static void SetEnabled(View view, bool enabled)
    {
        view.Enabled = enabled;
        view.Alpha = enabled ? 1f : WinoColors.Fraction(view.RequireContext(), Resource.Fraction.wino_m3_disabled_alpha);
    }

    private void RefreshState()
    {
        var vm = _viewModel;
        Show(_loading, vm.IsLoading);
        Show(_unavailable, vm.IsUnavailable && !vm.IsLoading);
        Show(_error, vm.HasLoadError && !vm.IsLoading && !vm.IsUnavailable);
        _errorMessage.Text = vm.LoadError;
        Show(_errorMessage, vm.HasLoadError);
        Show(_empty, vm.IsEmpty && !vm.IsLoading && !vm.IsUnavailable && !vm.HasLoadError);
        Show(_filtered, vm.ShowFilteredEmpty);
        Show(_scroll, vm.ShowContent);
    }

    private static void Show(View view, bool visible) => view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;

    /// <summary>Rebuilds the cards from the ViewModel's items; a card follows its item's ignore and new state.</summary>
    private void RefreshItems()
    {
        foreach (var (item, (_, handler)) in _cards) item.PropertyChanged -= handler;
        _cards.Clear();
        _list.RemoveAllViews();
        var context = this.RequireContext();
        int gap = WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_card_gap);
        foreach (var item in _viewModel.Items)
        {
            var captured = item;
            var card = new BriefingCard(context);
            card.Update(DailyBriefingCardContent.From(item));
            card.PrimaryClicked += (_, _) => _viewModel.ExecutePrimaryActionCommand.Execute(captured);
            card.IgnoreClicked += (_, _) => _viewModel.IgnoreCommand.Execute(captured);
            PropertyChangedEventHandler handler = (_, _) => _ = Ui(() => card.Update(DailyBriefingCardContent.From(captured)));
            item.PropertyChanged += handler;
            _cards[item] = (card, handler);
            _list.AddView(card, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = gap });
        }
    }

    private static ImageButton IconButton(Context context, string glyph, string description)
    {
        var button = new ImageButton(context) { ContentDescription = description };
        button.SetImageDrawable(new WinoGlyphDrawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.SecondaryText(context)));
        var circle = new GradientDrawable();
        circle.SetShape(ShapeType.Oval);
        circle.SetColor(Color.White);
        button.Background = new RippleDrawable(ColorStateList.ValueOf(WinoColors.SubtleFill(context)), circle, null);
        button.BackgroundTintList = ColorStateList.ValueOf(Color.Transparent);
        int size = WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button);
        button.LayoutParameters = new LayoutParams(size, size);
        return button;
    }

    private static View Divider(Context context)
    {
        var divider = new View(context);
        divider.SetBackgroundColor(WinoColors.Divider(context));
        divider.LayoutParameters = new LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_m3_stroke));
        return divider;
    }

    private static View StateGlyph(Context context, WinoIconGlyph icon, Color color)
    {
        var glyph = new WinoIconView(context) { Icon = icon, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_briefing_state_glyph), Gravity = GravityFlags.Center };
        glyph.SetTextColor(color);
        return glyph;
    }

    /// <summary>A centred state: glyph or spinner, title, message.</summary>
    private static View State(Context context, FrameLayout host, View visual, string? title, string message) => State(context, host, visual, title, message, out _);

    private static LinearLayout State(Context context, FrameLayout host, View visual, string? title, string message, out TextView body)
    {
        var column = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical, Visibility = ViewStates.Gone };
        column.SetGravity(GravityFlags.Center);
        WinoTokens.Pad(column, Resource.Dimension.wino_m3_briefing_state_padding, Resource.Dimension.wino_m3_briefing_state_padding,
            Resource.Dimension.wino_m3_briefing_state_padding, Resource.Dimension.wino_m3_briefing_state_padding);
        column.AddView(visual, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_m) });
        var heading = WinoTokens.Style(new TextView(context) { Text = title ?? string.Empty, Gravity = GravityFlags.Center }, WinoTextStyle.BodyLargeStrong);
        heading.Visibility = string.IsNullOrEmpty(title) ? ViewStates.Gone : ViewStates.Visible;
        column.AddView(heading);
        body = WinoTokens.Style(new TextView(context) { Text = message, Gravity = GravityFlags.Center }, WinoTextStyle.BodySecondary);
        body.SetTextColor(WinoColors.SecondaryText(context));
        column.AddView(body, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs) });
        host.AddView(column, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        return column;
    }

    /// <summary>
    /// One briefing card (Windows BriefingItemTemplate, UIKit WinoBriefingCardView) as a Material 3 outlined card:
    /// the headline with the "new" dot and the time, the summary, the urgency and label chips, the tonal primary action
    /// and the ignore toggle.
    /// </summary>
    private sealed class BriefingCard : LinearLayout
    {
        private readonly TextView _headline;
        private readonly View _newDot;
        private readonly TextView _time;
        private readonly TextView _summary;
        private readonly LinearLayout _chips;
        private readonly MaterialButton _primary;
        private readonly ImageButton _ignore;

        public BriefingCard(Context context) : base(context)
        {
            Orientation = global::Android.Widget.Orientation.Vertical;
            var surface = new GradientDrawable();
            surface.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_m3_briefing_card_radius));
            surface.SetColor(WinoColors.CardFill(context));
            surface.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_m3_stroke), WinoColors.CardStroke(context));
            Background = surface;
            WinoTokens.Pad(this, Resource.Dimension.wino_m3_briefing_card_padding_horizontal, Resource.Dimension.wino_m3_briefing_card_padding_vertical,
                Resource.Dimension.wino_m3_briefing_card_padding_horizontal, Resource.Dimension.wino_m3_briefing_card_padding_vertical);
            int lineGap = WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_card_line_gap);

            var top = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
            _headline = WinoTokens.Style(new TextView(context), WinoTextStyle.BodyLargeStrong);
            _headline.SetMaxLines(2);
            _headline.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            top.AddView(_headline, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            var dotFill = new GradientDrawable();
            dotFill.SetShape(ShapeType.Oval);
            dotFill.SetColor(WinoColors.Accent(context));
            int dot = WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_new_dot);
            _newDot = new View(context) { Background = dotFill, ContentDescription = Translator.DailyBriefing_New };
            top.AddView(_newDot, new LayoutParams(dot, dot) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s), TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_xs), MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_xs) });
            _time = WinoTokens.Style(new TextView(context), WinoTextStyle.Caption);
            _time.SetTextColor(WinoColors.SecondaryText(context));
            top.AddView(_time, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs) });
            AddView(top);

            _summary = WinoTokens.Style(new TextView(context), WinoTextStyle.BodySecondary);
            _summary.SetTextColor(WinoColors.SecondaryText(context));
            _summary.SetMaxLines(3);
            _summary.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            AddView(_summary, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = lineGap });

            var actions = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
            actions.SetGravity(GravityFlags.CenterVertical);
            _chips = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
            _chips.SetGravity(GravityFlags.CenterVertical);
            actions.AddView(_chips, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            _primary = new MaterialButton(context, null, Resource.Attribute.materialButtonStyle);
            _primary.SetAllCaps(false);
            _primary.InsetTop = 0;
            _primary.InsetBottom = 0;
            _primary.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_action_height));
            _primary.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_action_height));
            _primary.BackgroundTintList = ColorStateList.ValueOf(WinoColors.SecondaryContainer(context));
            _primary.SetTextColor(WinoColors.PrimaryText(context));
            _primary.IconTint = ColorStateList.ValueOf(WinoColors.PrimaryText(context));
            _primary.IconGravity = MaterialButton.IconGravityTextStart;
            WinoTokens.Style(_primary, WinoTextStyle.Label);
            _primary.SetTextColor(WinoColors.PrimaryText(context));
            _primary.Click += (_, _) => PrimaryClicked?.Invoke(this, EventArgs.Empty);
            actions.AddView(_primary, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
            _ignore = IconButton(context, string.Empty, string.Empty);
            _ignore.Click += (_, _) => IgnoreClicked?.Invoke(this, EventArgs.Empty);
            actions.AddView(_ignore);
            AddView(actions, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = lineGap });
        }

        public event EventHandler? PrimaryClicked;

        public event EventHandler? IgnoreClicked;

        public void Update(DailyBriefingCardContent card)
        {
            var context = this.RequireContext();
            _headline.Text = card.Headline;
            _time.Text = card.Time;
            _newDot.Visibility = card.IsNew ? ViewStates.Visible : ViewStates.Gone;
            _summary.Text = card.Summary ?? string.Empty;
            _summary.Visibility = string.IsNullOrWhiteSpace(card.Summary) ? ViewStates.Gone : ViewStates.Visible;

            _chips.RemoveAllViews();
            if (card.Urgency is { Length: > 0 } urgency)
                _chips.AddView(Chip(context, urgency, WinoColors.WithAlpha(WinoColors.Caution(context), 0.16), WinoColors.Caution(context)));
            foreach (var label in card.Labels)
                _chips.AddView(Chip(context, label, WinoColors.SubtleFill(context), WinoColors.SecondaryText(context)));

            _primary.Text = card.ActionText;
            _primary.Icon = new WinoGlyphDrawable(context, card.ActionGlyph, WinoTokens.Dp(context, Resource.Dimension.wino_m3_briefing_action_glyph), WinoColors.PrimaryText(context));
            _ignore.SetImageDrawable(new WinoGlyphDrawable(context, card.IgnoreGlyph, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small), WinoColors.SecondaryText(context)));
            _ignore.ContentDescription = card.IgnoreText;
            _ignore.Enabled = card.CanToggleIgnore;
            ContentDescription = string.Join(", ", new[] { card.Headline, card.IsNew ? Translator.DailyBriefing_New : null, card.Time, card.Summary, card.Urgency }
                .Concat(card.Labels).Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static TextView Chip(Context context, string text, Color fill, Color color)
        {
            var chip = WinoTokens.Style(new TextView(context) { Text = text, Gravity = GravityFlags.Center }, WinoTextStyle.Caption);
            chip.SetTextColor(color);
            chip.SetMaxLines(1);
            chip.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            var shape = new GradientDrawable();
            shape.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_m3_briefing_chip_radius));
            shape.SetColor(fill);
            chip.Background = shape;
            int padding = WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_chip_padding);
            chip.SetPadding(padding, 0, padding, 0);
            chip.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_chip_height));
            chip.ImportantForAccessibility = ImportantForAccessibility.No;
            chip.LayoutParameters = new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs) };
            return chip;
        }
    }
}
