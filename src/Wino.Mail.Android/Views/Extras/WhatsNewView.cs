using System.ComponentModel;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Google.Android.Material.Chip;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.WhatsNew;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.Android.Common;
using Wino.Platform.Android;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Extras;

/// <summary>
/// The What's New page (iOS WhatsNewPageViewController, Mac WhatsNewPageViewController, Windows WhatsNewPage) for a
/// Material full-screen dialog: a top app bar with Close and the title, then inside a Wino zone the version chips (a star
/// for highlighted releases), the feature card (the 1120×600 illustration, title and description) and the feature list.
/// From 600 dp the list (260 dp) sits beside the card as on the iPad and the Mac; narrower, the card comes first.
/// Content is exactly what <see cref="WhatsNewPageViewModel"/> provides.
/// </summary>
public sealed class WhatsNewView : LinearLayout
{
    private const float IllustrationAspect = 600f / 1120f;
    private const int ListWidthDp = 260;
    private const int SideBySideMinWidthDp = 600;

    private readonly WhatsNewPageViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly ChipGroup _versions;
    private readonly HorizontalScrollView _versionScroll;
    private readonly LinearLayout _columns;
    private readonly LinearLayout _features;
    private readonly ImageView _image;
    private readonly TextView _title;
    private readonly TextView _description;
    private readonly TextView _empty;
    private readonly Dictionary<WhatsNewRelease, Chip> _chips = new();
    private readonly Dictionary<WhatsNewFeature, FeatureRow> _rows = new();
    private bool _applyingChips;
    private bool _attached;

    public WhatsNewView(Context context, WhatsNewPageViewModel viewModel, IDispatcher dispatcher) : base(context)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        Orientation = global::Android.Widget.Orientation.Vertical;
        SetBackgroundColor(WinoColors.FromResource(context, Resource.Color.wino_backdrop));
        WinoInsets.Apply(this);

        // Top app bar: Close and the title.
        var bar = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_top_bar_height));
        WinoTokens.Pad(bar, Resource.Dimension.wino_space_xs, 0, Resource.Dimension.wino_space_m, 0);
        var close = IconButton(context, WinoIconGlyph.Dismiss, Translator.Buttons_Close);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        bar.AddView(close);
        var header = WinoTokens.Style(new TextView(context) { Text = Translator.WhatsNew_Header }, WinoTextStyle.TopAppBarTitle);
        header.SetMaxLines(1);
        header.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(header, true);
        bar.AddView(header, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
        AddView(bar, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // The zone holds the page; the empty message sits over it while there are no notes.
        var zone = new WinoZoneLayout(context);
        var zoneMargins = new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1);
        int gutter = WinoTokens.Px(context, Resource.Dimension.wino_space_s);
        zoneMargins.SetMargins(gutter, 0, gutter, gutter);
        AddView(zone, zoneMargins);

        var page = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical };
        WinoTokens.Pad(page, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_xl);

        _versions = new ChipGroup(context) { SingleLine = true, SingleSelection = true, SelectionRequired = true, ContentDescription = Translator.WhatsNew_VersionSelectorAutomationName };
        _versions.ChipSpacingHorizontal = WinoTokens.Px(context, Resource.Dimension.wino_space_s);
        _versionScroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
        _versionScroll.AddView(_versions);
        page.AddView(_versionScroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_m) });

        // Feature card: illustration, rule, title and description.
        var card = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical, ClipToOutline = true };
        var cardShape = new GradientDrawable();
        cardShape.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_m3_briefing_card_radius));
        cardShape.SetColor(WinoColors.CardFill(context));
        cardShape.SetStroke(Math.Max(1, WinoUnits.Dp(context, 1)), WinoColors.CardStroke(context));
        card.Background = cardShape;
        _image = new AspectImageView(context, IllustrationAspect) { ImportantForAccessibility = ImportantForAccessibility.No };
        _image.SetScaleType(ImageView.ScaleType.CenterCrop);
        _image.SetBackgroundColor(WinoColors.SubtleFill(context));
        card.AddView(_image, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var rule = new View(context);
        rule.SetBackgroundColor(WinoColors.CardStroke(context));
        card.AddView(rule, new LayoutParams(ViewGroup.LayoutParams.MatchParent, Math.Max(1, WinoUnits.Dp(context, 1))));
        var text = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical };
        WinoTokens.Pad(text, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l);
        _title = WinoTokens.Style(new TextView(context), WinoTextStyle.Subtitle);
        _title.SetTextColor(WinoColors.PrimaryText(context));
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(_title, true);
        text.AddView(_title, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
        _description = WinoTokens.Style(new TextView(context), WinoTextStyle.BodySecondary);
        text.AddView(_description, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(text, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Feature list.
        _features = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical, ContentDescription = Translator.WhatsNew_FeatureListAutomationName };

        int widthDp = context.Resources?.Configuration?.ScreenWidthDp ?? 0;
        bool sideBySide = widthDp >= SideBySideMinWidthDp;
        _columns = new LinearLayout(context) { Orientation = sideBySide ? global::Android.Widget.Orientation.Horizontal : global::Android.Widget.Orientation.Vertical };
        int gap = WinoTokens.Px(context, Resource.Dimension.wino_space_m);
        if (sideBySide)
        {
            _columns.AddView(_features, new LayoutParams(WinoUnits.Dp(context, ListWidthDp), ViewGroup.LayoutParams.WrapContent) { MarginEnd = gap });
            _columns.AddView(card, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        }
        else
        {
            _columns.AddView(card, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = gap });
            _columns.AddView(_features, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
        page.AddView(_columns, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var scroll = new NestedScrollView(context) { FillViewport = true };
        scroll.AddView(page, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        zone.AddView(scroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _empty = WinoTokens.Style(new TextView(context) { Text = Translator.WhatsNew_EmptyMessage, Gravity = GravityFlags.Center, Visibility = ViewStates.Gone }, WinoTextStyle.BodySecondary);
        int emptyPadding = WinoTokens.Px(context, Resource.Dimension.wino_space_xl);
        _empty.SetPadding(emptyPadding, 0, emptyPadding, 0);
        zone.AddView(_empty, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));

        _versions.CheckedStateChange += (_, _) =>
        {
            if (_applyingChips) return;
            var release = _chips.FirstOrDefault(pair => pair.Value.Checked).Key;
            if (release is not null && !ReferenceEquals(release, _viewModel.SelectedRelease)) _viewModel.SelectedRelease = release;
        };
    }

    /// <summary>Close was tapped.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Starts observing the ViewModel (the dialog was shown).</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        _viewModel.ReleasesLoaded += ReleasesLoaded;
        ShowState();
        RebuildChips();
        RebuildFeatures();
        ShowFeature();
    }

    /// <summary>Stops observing the ViewModel (the dialog closed).</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel.ReleasesLoaded -= ReleasesLoaded;
    }

    private void ReleasesLoaded(object? sender, EventArgs args) => _ = _dispatcher.ExecuteOnUIThread(RebuildChips);

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) => _ = _dispatcher.ExecuteOnUIThread(() =>
    {
        if (!_attached) return;
        switch (args.PropertyName)
        {
            case nameof(WhatsNewPageViewModel.HasReleases):
                ShowState();
                break;
            case nameof(WhatsNewPageViewModel.SelectedRelease):
                CheckSelectedChip();
                break;
            case nameof(WhatsNewPageViewModel.Features):
                RebuildFeatures();
                break;
            case nameof(WhatsNewPageViewModel.SelectedFeature):
                ShowFeature();
                break;
        }
    });

    private void ShowState()
    {
        bool has = _viewModel.HasReleases;
        _versionScroll.Visibility = has ? ViewStates.Visible : ViewStates.Gone;
        _columns.Visibility = has ? ViewStates.Visible : ViewStates.Gone;
        _empty.Visibility = has ? ViewStates.Gone : ViewStates.Visible;
    }

    private void RebuildChips()
    {
        _applyingChips = true;
        try
        {
            _versions.RemoveAllViews();
            _chips.Clear();
            foreach (var release in _viewModel.Releases)
            {
                var chip = WinoChips.Filter(Context!, release.Version, ReferenceEquals(release, _viewModel.SelectedRelease));
                chip.Id = View.GenerateViewId();
                if (release.IsStarred)
                {
                    chip.ChipIcon = WinoIcons.Drawable(Context!, WinoIconGlyph.Star, 12, WinoColors.Caution(Context!));
                    chip.ChipIconVisible = true;
                    chip.ContentDescription = string.Format(Translator.WhatsNew_StarredVersionAutomationName, release.Version);
                }
                _chips[release] = chip;
                _versions.AddView(chip);
            }
        }
        finally { _applyingChips = false; }
    }

    private void CheckSelectedChip()
    {
        _applyingChips = true;
        try
        {
            foreach (var (release, chip) in _chips) chip.Checked = ReferenceEquals(release, _viewModel.SelectedRelease);
        }
        finally { _applyingChips = false; }
    }

    private void RebuildFeatures()
    {
        _features.RemoveAllViews();
        _rows.Clear();
        foreach (var feature in _viewModel.Features)
        {
            var row = new FeatureRow(Context!, feature.Title, feature.Description) { IsChosen = ReferenceEquals(feature, _viewModel.SelectedFeature) };
            row.Click += (_, _) => _viewModel.SelectedFeature = feature;
            _rows[feature] = row;
            _features.AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = WinoUnits.Dp(Context!, 4) });
        }
    }

    private void ShowFeature()
    {
        var feature = _viewModel.SelectedFeature;
        foreach (var (item, row) in _rows) row.IsChosen = ReferenceEquals(item, feature);
        _title.Text = feature?.Title ?? string.Empty;
        _description.Text = feature?.Description ?? string.Empty;
        _image.SetImageBitmap(feature is null ? null : LoadIllustration(feature.Image));
    }

    /// <summary>Illustrations ship beside the release notes, extracted from the APK to the packaged resource root.</summary>
    private static Bitmap? LoadIllustration(string image)
    {
        if (string.IsNullOrWhiteSpace(image) || MainApplication.Services?.GetService(typeof(AndroidPaths)) is not AndroidPaths paths) return null;
        var path = System.IO.Path.Combine(paths.PackagedResourceRoot, "Assets", "WhatsNew", image);
        return File.Exists(path) ? BitmapFactory.DecodeFile(path) : null;
    }

    private static ImageButton IconButton(Context context, WinoIconGlyph glyph, string description)
    {
        var button = new ImageButton(context) { ContentDescription = description };
        button.SetImageDrawable(new WinoGlyphDrawable(context, WinoIcons.Glyph(glyph), WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.PrimaryText(context)));
        var circle = new GradientDrawable();
        circle.SetShape(ShapeType.Oval);
        circle.SetColor(Color.White);
        button.Background = new RippleDrawable(ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null, circle);
        int size = WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button);
        button.LayoutParameters = new LayoutParams(size, size);
        return button;
    }

    /// <summary>An image view whose height follows its width (the 1120×600 illustration).</summary>
    private sealed class AspectImageView(Context context, float aspect) : ImageView(context)
    {
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int width = MeasureSpec.GetSize(widthMeasureSpec);
            SetMeasuredDimension(width, (int)(width * aspect));
        }
    }

    /// <summary>A feature entry (iOS WinoFeatureRow): the title and a two-line description; the chosen one is filled.</summary>
    private sealed class FeatureRow : LinearLayout
    {
        private readonly GradientDrawable _fill = new();

        public FeatureRow(Context context, string title, string description) : base(context)
        {
            Orientation = global::Android.Widget.Orientation.Vertical;
            Clickable = true;
            Focusable = true;
            ContentDescription = $"{title}. {description}";
            WinoTokens.Pad(this, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_sm);
            SetMinimumHeight(WinoUnits.Dp(context, 48));
            _fill.SetCornerRadius(WinoUnits.DpF(context, 8));
            Background = new RippleDrawable(ColorStateList.ValueOf(WinoColors.SubtleFill(context)), _fill, null);
            var titleLabel = WinoTokens.Style(new TextView(context) { Text = title }, WinoTextStyle.BodyStrong);
            titleLabel.SetTextColor(WinoColors.PrimaryText(context));
            AddView(titleLabel);
            var descriptionLabel = WinoTokens.Style(new TextView(context) { Text = description }, WinoTextStyle.Caption);
            descriptionLabel.SetTextColor(WinoColors.SecondaryText(context));
            descriptionLabel.SetMaxLines(2);
            descriptionLabel.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            AddView(descriptionLabel);
            IsChosen = false;
        }

        public bool IsChosen
        {
            set
            {
                var context = this.RequireContext();
                _fill.SetColor(value ? WinoColors.SelectedFill(context) : Color.Transparent);
                Selected = value;
            }
        }
    }
}
