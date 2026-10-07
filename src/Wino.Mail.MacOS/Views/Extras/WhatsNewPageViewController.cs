using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.WhatsNew;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// The What's New page (Windows WhatsNewPage): title row, version selector, a 260pt feature list
/// and the feature card with its 560×300 illustration slot, title and description, all inside a
/// Wino zone (margin 0/8/8 under the 40pt title bar, padding 8/24/24).
/// </summary>
public sealed class WhatsNewPageViewController(WhatsNewPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<WhatsNewPageViewModel>(viewModel, dispatcher, logger)
{
    private NSStackView _versions = null!;
    private NSStackView _features = null!;
    private NSStackView _columns = null!;
    private NSTextField _empty = null!;
    private WinoAspectFillImageView _image = null!;
    private NSTextField _title = null!;
    private NSTextField _description = null!;
    private readonly Dictionary<WhatsNewRelease, WinoVersionTab> _tabs = new();
    private readonly Dictionary<WhatsNewFeature, WinoFeatureRow> _rows = new();

    public override void LoadView()
    {
        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var zone = new WinoZoneView();
        WinoLayout.Fill(zone, root, 40, 8, 8, 8);

        var icon = new WinoIconView(WinoIconGlyph.Announcement, 22, WinoStyle.Accent);
        var header = WinoStyle.Label(Translator.WhatsNew_Header, NSFont.SystemFontOfSize(24, NSFontWeight.Semibold));
        var titleRow = WinoLayout.HStack(10, icon, header);
        titleRow.HeightAnchor.ConstraintEqualTo(40).Active = true;

        _versions = WinoLayout.HStack(4);
        WinoAccessibility.Label(_versions, Translator.WhatsNew_VersionSelectorAutomationName);

        // Feature list: 260pt, rows 4pt apart, scrolls when a release has many entries.
        _features = WinoLayout.VStack(4);
        WinoAccessibility.Label(_features, Translator.WhatsNew_FeatureListAutomationName);
        var document = new FlippedView();
        document.AddSubview(_features);
        var listScroll = new NSScrollView
        {
            DocumentView = document,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        listScroll.WidthAnchor.ConstraintEqualTo(260).Active = true;
        NSLayoutConstraint.ActivateConstraints(
        [
            _features.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _features.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _features.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            document.WidthAnchor.ConstraintEqualTo(listScroll.ContentView.WidthAnchor),
            document.HeightAnchor.ConstraintGreaterThanOrEqualTo(_features.HeightAnchor)
        ]);

        // Feature card: image slot, rule, scrolling title and description.
        var card = new WinoSurfaceView { CornerRadius = 8, Fill = WinoBriefingCardView.CardFill, Stroke = WinoBriefingCardView.CardStroke };
        _image = new WinoAspectFillImageView();
        var rule = new WinoSeparator { Fill = WinoBriefingCardView.CardStroke };
        _title = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(17, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        _description = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(8, _title, _description);
        text.EdgeInsets = new NSEdgeInsets(16, 20, 20, 20);
        var textDocument = new FlippedView();
        textDocument.AddSubview(text);
        var textScroll = new NSScrollView
        {
            DocumentView = textDocument,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        card.AddSubview(_image);
        card.AddSubview(rule);
        card.AddSubview(textScroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            _image.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor), _image.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor),
            _image.TopAnchor.ConstraintEqualTo(card.TopAnchor), _image.HeightAnchor.ConstraintEqualTo(300),
            rule.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor), rule.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor),
            rule.TopAnchor.ConstraintEqualTo(_image.BottomAnchor),
            textScroll.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor), textScroll.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor),
            textScroll.TopAnchor.ConstraintEqualTo(rule.BottomAnchor), textScroll.BottomAnchor.ConstraintEqualTo(card.BottomAnchor),
            text.LeadingAnchor.ConstraintEqualTo(textDocument.LeadingAnchor), text.TrailingAnchor.ConstraintEqualTo(textDocument.TrailingAnchor),
            text.TopAnchor.ConstraintEqualTo(textDocument.TopAnchor),
            textDocument.WidthAnchor.ConstraintEqualTo(textScroll.ContentView.WidthAnchor),
            textDocument.HeightAnchor.ConstraintGreaterThanOrEqualTo(text.HeightAnchor),
            _title.TrailingAnchor.ConstraintEqualTo(text.TrailingAnchor, -20),
            _description.TrailingAnchor.ConstraintEqualTo(text.TrailingAnchor, -20)
        ]);

        _columns = WinoLayout.HStack(8, listScroll, card);
        _columns.Alignment = NSLayoutAttribute.Top;
        _columns.Distribution = NSStackViewDistribution.Fill;
        _columns.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        listScroll.TopAnchor.ConstraintEqualTo(_columns.TopAnchor).Active = true;
        listScroll.BottomAnchor.ConstraintEqualTo(_columns.BottomAnchor).Active = true;
        card.TopAnchor.ConstraintEqualTo(_columns.TopAnchor).Active = true;
        card.BottomAnchor.ConstraintEqualTo(_columns.BottomAnchor).Active = true;

        var page = WinoLayout.VStack(12, titleRow, _versions, _columns);
        page.EdgeInsets = new NSEdgeInsets(8, 24, 24, 24);
        _columns.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor, 24).Active = true;
        _columns.TrailingAnchor.ConstraintEqualTo(page.TrailingAnchor, -24).Active = true;
        WinoLayout.Fill(page, zone.ContentView);

        _empty = WinoStyle.Label(Translator.WhatsNew_EmptyMessage, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _empty.Alignment = NSTextAlignment.Center;
        zone.ContentView.AddSubview(_empty);
        NSLayoutConstraint.ActivateConstraints(
        [
            _empty.CenterXAnchor.ConstraintEqualTo(zone.ContentView.CenterXAnchor),
            _empty.CenterYAnchor.ConstraintEqualTo(zone.ContentView.CenterYAnchor),
            _empty.WidthAnchor.ConstraintLessThanOrEqualTo(zone.ContentView.WidthAnchor, 1, -96)
        ]);
        View = root;

        ViewModel.ReleasesLoaded += ReleasesLoaded;
        Bindings.Own(new ActionDisposable(() => ViewModel.ReleasesLoaded -= ReleasesLoaded));
        Bindings.Own(new PropertyBinding<WhatsNewPageViewModel, bool>(ViewModel, nameof(ViewModel.HasReleases), vm => vm.HasReleases, has =>
        {
            _versions.Hidden = !has;
            _columns.Hidden = !has;
            _empty.Hidden = has;
        }, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<WhatsNewPageViewModel, WhatsNewRelease?>(ViewModel, nameof(ViewModel.SelectedRelease), vm => vm.SelectedRelease, release =>
        {
            foreach (var (item, tab) in _tabs) tab.IsSelected = ReferenceEquals(item, release);
        }, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<WhatsNewPageViewModel, IReadOnlyList<WhatsNewFeature>>(ViewModel, nameof(ViewModel.Features), vm => vm.Features, RebuildFeatures, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<WhatsNewPageViewModel, WhatsNewFeature?>(ViewModel, nameof(ViewModel.SelectedFeature), vm => vm.SelectedFeature, ShowFeature, Dispatcher, ReportError));
    }

    private void ReleasesLoaded(object? sender, EventArgs args)
    {
        foreach (var tab in _tabs.Values) tab.RemoveFromSuperview();
        _tabs.Clear();
        foreach (var release in ViewModel.Releases)
        {
            var tab = new WinoVersionTab(release.Version, release.IsStarred)
            {
                IsSelected = ReferenceEquals(release, ViewModel.SelectedRelease),
                AccessibilityLabel = release.IsStarred ? string.Format(Translator.WhatsNew_StarredVersionAutomationName, release.Version) : release.Version
            };
            tab.Clicked += (_, _) => ViewModel.SelectedRelease = release;
            _tabs[release] = tab;
            _versions.AddArrangedSubview(tab);
        }
    }

    private void RebuildFeatures(IReadOnlyList<WhatsNewFeature> features)
    {
        foreach (var row in _rows.Values) row.RemoveFromSuperview();
        _rows.Clear();
        foreach (var feature in features)
        {
            var row = new WinoFeatureRow(feature.Title, feature.Description) { IsSelected = ReferenceEquals(feature, ViewModel.SelectedFeature) };
            row.Clicked += (_, _) => ViewModel.SelectedFeature = feature;
            _rows[feature] = row;
            _features.AddArrangedSubview(row);
            row.LeadingAnchor.ConstraintEqualTo(_features.LeadingAnchor).Active = true;
            row.TrailingAnchor.ConstraintEqualTo(_features.TrailingAnchor).Active = true;
        }
    }

    private void ShowFeature(WhatsNewFeature? feature)
    {
        foreach (var (item, row) in _rows) row.IsSelected = ReferenceEquals(item, feature);
        _title.StringValue = feature?.Title ?? string.Empty;
        _description.StringValue = feature?.Description ?? string.Empty;
        _image.Image = feature is null ? null : LoadIllustration(feature.Image);
    }

    /// <summary>
    /// Illustrations ship beside the release notes (Assets/WhatsNew next to the assembly, where the
    /// shared WhatsNewService reads them) and as a fallback in the bundle's Resources folder.
    /// </summary>
    private static NSImage? LoadIllustration(string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        string?[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsNew", image),
            NSBundle.MainBundle.ResourcePath is { } resources ? Path.Combine(resources, "WhatsNew", image) : null
        ];
        var path = candidates.FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
        return path is null ? null : new NSImage(path);
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}
