using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.WhatsNew;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Extras;

/// <summary>
/// The What's New page (Windows WhatsNewPage, macOS WhatsNewPageViewController): title row, a pill per release (a star
/// marks highlighted releases), the release's features as a 260 px list beside the selected feature's card with its
/// illustration, title and description; an empty message when no notes ship with the app.
/// </summary>
internal sealed class WhatsNewView : WinoView<WhatsNewPageViewModel>
{
    private const int ListWidth = 260;
    private const int IllustrationHeight = 300;

    private readonly Gtk.Stack _root;
    private readonly Gtk.Box _versions;
    private readonly Gtk.ListBox _features;
    private readonly Gtk.Picture _picture;
    private readonly Gtk.Label _title;
    private readonly Gtk.Label _description;
    private readonly List<(WhatsNewRelease Release, Gtk.ToggleButton Pill)> _pills = [];
    private readonly List<(WhatsNewFeature Feature, Gtk.ListBoxRow Row)> _rows = [];
    private Wino.Presentation.Bindings.BindingScope? _pillScope;
    private bool _syncing;
    private int _pictureVersion;

    public WhatsNewView(WhatsNewPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var vm = ViewModel;

        var icon = new WinoIcon(WinoIconGlyph.Announcement, 22, WinoIconMode.Mono).Root;
        icon.AddCssClass("accent");
        var header = Gtk.Label.New(Translator.WhatsNew_Header);
        header.AddCssClass("title-2");
        header.AccessibleRole = Gtk.AccessibleRole.Heading;
        header.SetXalign(0);
        var titleRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        titleRow.Append(icon);
        titleRow.Append(header);

        _versions = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        WinoA11y.Label(_versions, Translator.WhatsNew_VersionSelectorAutomationName);
        _versions.AccessibleRole = Gtk.AccessibleRole.Group;
        var versionScroller = Gtk.ScrolledWindow.New();
        versionScroller.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
        versionScroller.SetChild(_versions);

        _features = Gtk.ListBox.New();
        _features.AddCssClass("navigation-sidebar");
        _features.SetSelectionMode(Gtk.SelectionMode.Single);
        WinoA11y.Label(_features, Translator.WhatsNew_FeatureListAutomationName);
        var features = _features;
        Bindings.Own(GtkSignals.Connect<Gtk.ListBox, Gtk.ListBox.RowSelectedSignalArgs>(
            h => features.OnRowSelected += h, h => features.OnRowSelected -= h, (_, args) => RowSelected(args.Row)));
        var listScroller = Gtk.ScrolledWindow.New();
        listScroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        listScroller.SetSizeRequest(ListWidth, -1);
        listScroller.SetChild(_features);

        _picture = Gtk.Picture.New();
        _picture.SetContentFit(Gtk.ContentFit.Cover);
        _picture.SetCanShrink(true);
        _picture.SetSizeRequest(-1, IllustrationHeight);
        _picture.SetVexpand(false);
        _title = Gtk.Label.New(string.Empty);
        _title.AddCssClass("title-4");
        _title.SetWrap(true);
        _title.SetXalign(0);
        _title.SetSelectable(false);
        _description = Gtk.Label.New(string.Empty);
        _description.AddCssClass("dim-label");
        _description.SetWrap(true);
        _description.SetXalign(0);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        text.SetMarginTop(16);
        text.SetMarginBottom(20);
        text.SetMarginStart(20);
        text.SetMarginEnd(20);
        text.Append(_title);
        text.Append(_description);
        var textScroller = Gtk.ScrolledWindow.New();
        textScroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        textScroller.SetVexpand(true);
        textScroller.SetChild(text);
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("card");
        card.SetOverflow(Gtk.Overflow.Hidden);
        card.SetHexpand(true);
        card.Append(_picture);
        card.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        card.Append(textScroller);

        var columns = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        columns.SetVexpand(true);
        columns.Append(listScroller);
        columns.Append(card);

        var page = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        page.SetMarginTop(8);
        page.SetMarginBottom(24);
        page.SetMarginStart(24);
        page.SetMarginEnd(24);
        page.Append(titleRow);
        page.Append(versionScroller);
        page.Append(columns);

        var empty = Adw.StatusPage.New();
        empty.SetDescription(System.Security.SecurityElement.Escape(Translator.WhatsNew_EmptyMessage));

        _root = Gtk.Stack.New();
        _root.AddNamed(page, "notes");
        _root.AddNamed(empty, "empty");
        _root.SetVisibleChildName("empty");

        EventHandler loaded = (_, _) => RunOnUI(RebuildVersions);
        vm.ReleasesLoaded += loaded;
        Bindings.Own(new ActionDisposable(() => vm.ReleasesLoaded -= loaded));
        Bind.Bind(nameof(vm.HasReleases), static s => s.HasReleases, has => _root.SetVisibleChildName(has ? "notes" : "empty"));
        Bind.Bind(nameof(vm.SelectedRelease), static s => s.SelectedRelease, ShowRelease);
        Bind.Bind(nameof(vm.Features), static s => s.Features, RebuildFeatures);
        Bind.Bind(nameof(vm.SelectedFeature), static s => s.SelectedFeature, ShowFeature);
    }

    public override Gtk.Widget Root => _root;

    private void RebuildVersions()
    {
        foreach (var (_, pill) in _pills) _versions.Remove(pill);
        _pills.Clear();
        _pillScope?.Dispose();
        _pillScope = Bindings.Own(new Wino.Presentation.Bindings.BindingScope());

        Gtk.ToggleButton? group = null;
        foreach (var release in ViewModel.Releases)
        {
            var label = Gtk.Label.New(release.Version);
            var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            if (release.IsStarred) content.Append(new WinoIcon(WinoIconGlyph.StarFilled, 12, WinoIconMode.Mono).Root);
            content.Append(label);
            var pill = Gtk.ToggleButton.New();
            pill.SetChild(content);
            pill.AddCssClass("wino-whatsnew-version");
            if (group is null) group = pill;
            else pill.SetGroup(group);
            WinoA11y.Label(pill, release.IsStarred ? string.Format(Translator.WhatsNew_StarredVersionAutomationName, release.Version) : release.Version);
            var item = release;
            var button = pill;
            _pillScope.Own(GtkSignals.OnToggled(pill, () =>
            {
                if (_syncing || !button.GetActive()) return;
                ViewModel.SelectedRelease = item;
            }));
            _pills.Add((release, pill));
            _versions.Append(pill);
        }
        ShowRelease(ViewModel.SelectedRelease);
    }

    private void ShowRelease(WhatsNewRelease? release)
    {
        _syncing = true;
        try
        {
            foreach (var (item, pill) in _pills)
            {
                var selected = ReferenceEquals(item, release);
                if (pill.GetActive() != selected) pill.SetActive(selected);
                if (selected) pill.AddCssClass("suggested-action");
                else pill.RemoveCssClass("suggested-action");
            }
        }
        finally { _syncing = false; }
    }

    private void RebuildFeatures(List<WhatsNewFeature> features)
    {
        _syncing = true;
        try
        {
            _features.RemoveAll();
            _rows.Clear();
            foreach (var feature in features)
            {
                var title = Gtk.Label.New(feature.Title);
                title.AddCssClass("heading");
                title.SetWrap(true);
                title.SetXalign(0);
                var description = Gtk.Label.New(feature.Description);
                description.AddCssClass("caption");
                description.AddCssClass("dim-label");
                description.SetWrap(true);
                description.SetXalign(0);
                description.SetLines(3);
                description.SetEllipsize(Pango.EllipsizeMode.End);
                var box = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
                box.SetMarginTop(12);
                box.SetMarginBottom(12);
                box.SetMarginStart(8);
                box.SetMarginEnd(8);
                box.Append(title);
                box.Append(description);
                var row = Gtk.ListBoxRow.New();
                row.SetChild(box);
                WinoA11y.Label(row, string.IsNullOrWhiteSpace(feature.Description) ? feature.Title : $"{feature.Title}. {feature.Description}");
                _rows.Add((feature, row));
                _features.Append(row);
            }
        }
        finally { _syncing = false; }
        ShowFeature(ViewModel.SelectedFeature);
    }

    private void RowSelected(Gtk.ListBoxRow? row)
    {
        if (_syncing || row is null) return;
        foreach (var (feature, item) in _rows)
        {
            if (!ReferenceEquals(item, row)) continue;
            if (!ReferenceEquals(ViewModel.SelectedFeature, feature)) ViewModel.SelectedFeature = feature;
            return;
        }
    }

    private void ShowFeature(WhatsNewFeature? feature)
    {
        _syncing = true;
        try
        {
            var row = _rows.FirstOrDefault(entry => ReferenceEquals(entry.Feature, feature)).Row;
            if (row is null) _features.UnselectAll();
            else if (!ReferenceEquals(_features.GetSelectedRow(), row)) _features.SelectRow(row);
        }
        finally { _syncing = false; }

        _title.SetText(feature?.Title ?? string.Empty);
        _description.SetText(feature?.Description ?? string.Empty);
        LoadIllustration(feature?.Image);
    }

    private void LoadIllustration(string? image)
    {
        var version = ++_pictureVersion;
        _picture.SetPaintable(null);
        if (string.IsNullOrWhiteSpace(image)) return;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsNew", image);
        Observe(LoadIllustrationAsync(path, version));
    }

    private async Task LoadIllustrationAsync(string path, int version)
    {
        var scaled = await Task.Run(() => File.Exists(path) ? ThemeImaging.DecodeToFit(path, 1120, 600) : null);
        if (scaled is null) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased || version != _pictureVersion) return;
            _picture.SetPaintable(ThemeImaging.ToTexture(scaled));
        });
    }

#if DEBUG
    public string DebugDescribe()
    {
        var selected = ViewModel.SelectedRelease?.Version ?? "none";
        var index = ViewModel.SelectedFeature is { } feature ? ViewModel.Features.IndexOf(feature) : -1;
        return $"releases={ViewModel.Releases.Count} versions={string.Join(',', ViewModel.Releases.Select(r => (r.IsStarred ? "*" : string.Empty) + r.Version))} "
               + $"selected={selected} features={ViewModel.Features.Count} feature={index} title={ViewModel.SelectedFeature?.Title} "
               + $"picture={_picture.GetPaintable() is not null} empty={!ViewModel.HasReleases}\n";
    }

    public void DebugSelect(string version)
    {
        var release = ViewModel.Releases.FirstOrDefault(item => item.Version == version);
        if (release is not null) ViewModel.SelectedRelease = release;
    }

    public void DebugNext()
    {
        var features = ViewModel.Features;
        if (features.Count == 0) return;
        var index = ViewModel.SelectedFeature is { } feature ? features.IndexOf(feature) : -1;
        ViewModel.SelectedFeature = features[(index + 1) % features.Count];
    }
#endif
}
