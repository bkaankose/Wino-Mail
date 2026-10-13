using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.WhatsNew;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Extras;

/// <summary>
/// The What's New page (Mac WhatsNewPageViewController, Windows WhatsNewPage) inside a Wino zone: the title row, the
/// version selector, the feature card (the 1120×600 illustration from the bundle's Assets/WhatsNew, title and
/// description) and the feature list. In a regular-width sheet the list (260pt) sits beside the card as on the Mac; in
/// a compact width the card comes first and the list follows, all in one vertical scroll.
/// </summary>
public sealed class WhatsNewPageViewController(WhatsNewPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<WhatsNewPageViewModel>(viewModel, dispatcher, logger)
{
    private const double IllustrationAspect = 600.0 / 1120.0;

    private readonly UIStackView _versions = WinoLayout.HStack(6);
    private readonly UIStackView _features = WinoLayout.VStack(4);
    private readonly UIStackView _columns = WinoLayout.HStack(12);
    private readonly UIView _listColumn = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly WinoSurfaceView _card = new() { CornerRadius = 8, Fill = WinoBriefingCardView.CardFill, Stroke = WinoBriefingCardView.CardStroke };
    private readonly UILabel _empty = WinoStyle.Label(Translator.WhatsNew_EmptyMessage, WinoStyle.Body, WinoStyle.SecondaryText, 0);
    private readonly UIImageView _image = new() { TranslatesAutoresizingMaskIntoConstraints = false, ContentMode = UIViewContentMode.ScaleAspectFill, ClipsToBounds = true };
    private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Scaled(19, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
    private readonly UILabel _description = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
    private readonly Dictionary<WhatsNewRelease, WinoVersionTab> _tabs = new();
    private readonly Dictionary<WhatsNewFeature, WinoFeatureRow> _rows = new();
    private NSLayoutConstraint? _listWidth;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
        var zone = new WinoZoneView();
        root.AddSubview(zone);
        var gutter = WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.LeadingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.LeadingAnchor, (nfloat)gutter),
            zone.TrailingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TrailingAnchor, (nfloat)(-gutter)),
            zone.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor, (nfloat)gutter),
            zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, (nfloat)(-gutter))
        ]);

        var icon = new WinoIconView(WinoIconGlyph.Announcement, 24, WinoStyle.Accent);
        WinoLayout.Size(icon, 24, 24);
        var header = WinoStyle.Label(Translator.WhatsNew_Header, WinoStyle.Scaled(24, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        header.AccessibilityTraits = UIAccessibilityTrait.Header;
        var titleRow = WinoLayout.HStack(10, icon, header);

        var versionScroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, ShowsHorizontalScrollIndicator = false };
        versionScroll.AddSubview(_versions);
        NSLayoutConstraint.ActivateConstraints(
        [
            _versions.LeadingAnchor.ConstraintEqualTo(versionScroll.ContentLayoutGuide.LeadingAnchor),
            _versions.TrailingAnchor.ConstraintEqualTo(versionScroll.ContentLayoutGuide.TrailingAnchor),
            _versions.TopAnchor.ConstraintEqualTo(versionScroll.ContentLayoutGuide.TopAnchor),
            _versions.BottomAnchor.ConstraintEqualTo(versionScroll.ContentLayoutGuide.BottomAnchor),
            _versions.HeightAnchor.ConstraintEqualTo(versionScroll.FrameLayoutGuide.HeightAnchor),
            versionScroll.HeightAnchor.ConstraintEqualTo(36)
        ]);
        WinoAccessibility.Label(_versions, Translator.WhatsNew_VersionSelectorAutomationName);

        // Feature card: illustration, rule, title and description.
        var rule = new WinoSeparator { Fill = WinoBriefingCardView.CardStroke };
        var text = WinoLayout.Insets(WinoLayout.VStack(8, _title, _description), 16, 20, 20, 20);
        text.Alignment = UIStackViewAlignment.Fill;
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _image.BackgroundColor = WinoStyle.SubtleFill;
        var cardStack = WinoLayout.VStack(0, _image, rule, text);
        cardStack.Alignment = UIStackViewAlignment.Fill;
        _image.HeightAnchor.ConstraintEqualTo(_image.WidthAnchor, (nfloat)IllustrationAspect).Active = true;
        WinoLayout.Fill(cardStack, _card);

        // Feature list.
        _features.Alignment = UIStackViewAlignment.Fill;
        WinoAccessibility.Label(_features, Translator.WhatsNew_FeatureListAutomationName);
        _listColumn.AddSubview(_features);
        NSLayoutConstraint.ActivateConstraints(
        [
            _features.LeadingAnchor.ConstraintEqualTo(_listColumn.LeadingAnchor),
            _features.TrailingAnchor.ConstraintEqualTo(_listColumn.TrailingAnchor),
            _features.TopAnchor.ConstraintEqualTo(_listColumn.TopAnchor),
            _features.BottomAnchor.ConstraintLessThanOrEqualTo(_listColumn.BottomAnchor)
        ]);
        _listWidth = _listColumn.WidthAnchor.ConstraintEqualTo(260);

        _columns.Alignment = UIStackViewAlignment.Top;
        _columns.Distribution = UIStackViewDistribution.Fill;

        var page = WinoLayout.Insets(WinoLayout.VStack(14, titleRow, versionScroll, _columns), 16, 20, 24, 20);
        page.Alignment = UIStackViewAlignment.Fill;
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
        scroll.AddSubview(page);
        NSLayoutConstraint.ActivateConstraints(
        [
            page.LeadingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.LeadingAnchor),
            page.TrailingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TrailingAnchor),
            page.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor),
            page.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor),
            page.WidthAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.WidthAnchor)
        ]);
        WinoLayout.Fill(scroll, zone.ContentView);

        _empty.TextAlignment = UITextAlignment.Center;
        zone.ContentView.AddSubview(_empty);
        NSLayoutConstraint.ActivateConstraints(
        [
            _empty.CenterXAnchor.ConstraintEqualTo(zone.ContentView.CenterXAnchor),
            _empty.CenterYAnchor.ConstraintEqualTo(zone.ContentView.CenterYAnchor),
            _empty.WidthAnchor.ConstraintLessThanOrEqualTo(zone.ContentView.WidthAnchor, 1, -48)
        ]);
        View = root;
        ArrangeColumns();
        root.RegisterForTraitChanges([typeof(UITraitHorizontalSizeClass)], (IUITraitEnvironment _, UITraitCollection _) => ArrangeColumns());

        ViewModel.ReleasesLoaded += ReleasesLoaded;
        Bindings.Own(new ActionDisposable(() => ViewModel.ReleasesLoaded -= ReleasesLoaded));
        Bind(nameof(ViewModel.HasReleases), vm => vm.HasReleases, has =>
        {
            versionScroll.Hidden = !has;
            _columns.Hidden = !has;
            _empty.Hidden = has;
        });
        Bind(nameof(ViewModel.SelectedRelease), vm => vm.SelectedRelease, release =>
        {
            foreach (var (item, tab) in _tabs) tab.IsSelected = ReferenceEquals(item, release);
        });
        Bind(nameof(ViewModel.Features), vm => vm.Features, RebuildFeatures);
        Bind(nameof(ViewModel.SelectedFeature), vm => vm.SelectedFeature, ShowFeature);
    }

    /// <summary>List beside the card in a regular width (Mac layout); card above the list in a compact width.</summary>
    private void ArrangeColumns()
    {
        bool regular = (ViewIfLoaded?.TraitCollection ?? TraitCollection).HorizontalSizeClass == UIUserInterfaceSizeClass.Regular;
        foreach (var view in _columns.ArrangedSubviews) _columns.RemoveArrangedSubview(view);
        _columns.Axis = regular ? UILayoutConstraintAxis.Horizontal : UILayoutConstraintAxis.Vertical;
        _columns.Alignment = regular ? UIStackViewAlignment.Top : UIStackViewAlignment.Fill;
        if (regular)
        {
            _columns.AddArrangedSubview(_listColumn);
            _columns.AddArrangedSubview(_card);
        }
        else
        {
            _columns.AddArrangedSubview(_card);
            _columns.AddArrangedSubview(_listColumn);
        }
        if (_listWidth is not null) _listWidth.Active = regular;
    }

    private void ReleasesLoaded(object? sender, EventArgs args) => _ = Dispatcher.ExecuteOnUIThread(() =>
    {
        foreach (var tab in _tabs.Values)
        {
            _versions.RemoveArrangedSubview(tab);
            tab.RemoveFromSuperview();
        }
        _tabs.Clear();
        foreach (var release in ViewModel.Releases)
        {
            var tab = new WinoVersionTab(release.Version, release.IsStarred) { IsSelected = ReferenceEquals(release, ViewModel.SelectedRelease) };
            WinoAccessibility.Label(tab, release.IsStarred ? string.Format(Translator.WhatsNew_StarredVersionAutomationName, release.Version) : release.Version);
            tab.Clicked += (_, _) => ViewModel.SelectedRelease = release;
            _tabs[release] = tab;
            _versions.AddArrangedSubview(tab);
        }
    });

    private void RebuildFeatures(IReadOnlyList<WhatsNewFeature> features)
    {
        foreach (var row in _rows.Values)
        {
            _features.RemoveArrangedSubview(row);
            row.RemoveFromSuperview();
        }
        _rows.Clear();
        foreach (var feature in features)
        {
            var row = new WinoFeatureRow(feature.Title, feature.Description) { IsSelected = ReferenceEquals(feature, ViewModel.SelectedFeature) };
            row.Clicked += (_, _) => ViewModel.SelectedFeature = feature;
            _rows[feature] = row;
            _features.AddArrangedSubview(row);
        }
    }

    private void ShowFeature(WhatsNewFeature? feature)
    {
        foreach (var (item, row) in _rows) row.IsSelected = ReferenceEquals(item, feature);
        _title.Text = feature?.Title ?? string.Empty;
        _description.Text = feature?.Description ?? string.Empty;
        _image.Image = feature is null ? null : LoadIllustration(feature.Image);
    }

    /// <summary>
    /// Illustrations ship beside the release notes at Assets/WhatsNew in the app bundle root, where the shared
    /// WhatsNewService reads the JSON (AppContext.BaseDirectory is the .app folder on iOS).
    /// </summary>
    private static UIImage? LoadIllustration(string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        string?[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsNew", image),
            NSBundle.MainBundle.ResourcePath is { } resources ? Path.Combine(resources, "Assets", "WhatsNew", image) : null
        ];
        return candidates.FirstOrDefault(candidate => candidate is not null && File.Exists(candidate)) is { } path ? UIImage.FromFile(path) : null;
    }

#if DEBUG
    internal string Dump()
        => $"releases={_tabs.Count} features={_rows.Count} selected='{_title.Text}' image={(_image.Image is null ? "none" : $"{_image.Image.Size.Width}x{_image.Image.Size.Height}")} axis={_columns.Axis}";
#endif
}

/// <summary>A version capsule of the What's New selector (Mac WinoVersionTab): the version, a star for highlighted releases.</summary>
internal sealed class WinoVersionTab : WinoPressableView
{
    private readonly UILabel _label;
    private readonly WinoIconView? _star;
    private bool _selected;

    public WinoVersionTab(string version, bool starred)
    {
        CornerRadius = 16;
        _label = WinoStyle.Label(version, WinoStyle.Fixed(14, UIFontWeight.Medium));
        var row = WinoLayout.HStack(4, _label);
        if (starred)
        {
            _star = new WinoIconView(WinoIconGlyph.Star, 12, WinoStyle.Caution);
            WinoLayout.Size(_star, 12, 12);
            row.InsertArrangedSubview(_star, 0);
        }
        WinoLayout.Fill(WinoLayout.Insets(row, 6, 14, 6, 14), this);
        HeightAnchor.ConstraintEqualTo(32).Active = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        Apply();
    }

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Apply(); }
    }

    private void Apply()
    {
        Fill = _selected ? WinoStyle.Accent : WinoStyle.SubtleFill;
        _label.TextColor = _selected ? UIColor.White : WinoStyle.PrimaryText;
        AccessibilityTraits = _selected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }
}

/// <summary>A feature entry of the What's New list (Mac WinoFeatureRow): title and a two-line description.</summary>
internal sealed class WinoFeatureRow : WinoPressableView
{
    private bool _selected;

    public WinoFeatureRow(string title, string description)
    {
        CornerRadius = 8;
        var titleLabel = WinoStyle.Label(title, WinoStyle.Scaled(15, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        var descriptionLabel = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 2);
        var stack = WinoLayout.Insets(WinoLayout.VStack(2, titleLabel, descriptionLabel), 10, 12, 10, 12);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this);
        HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        IsAccessibilityElement = true;
        AccessibilityLabel = title;
        AccessibilityHint = description;
        Apply();
    }

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Apply(); }
    }

    private void Apply()
    {
        Fill = _selected ? WinoStyle.SelectedFill : UIColor.Clear;
        AccessibilityTraits = _selected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }
}
