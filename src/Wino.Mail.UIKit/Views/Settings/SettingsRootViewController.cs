using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// The Settings sheet's first page on iPhone and iPad (the Mac Settings sidebar and the Windows settings pane): a grouped
/// list in the Windows groups, names and order inside a Wino zone, with settings search stacked under the compact
/// navigation bar. Rows use the iOS inset-grouped density (44pt rows, 22pt glyph, 13pt section headers). A row pushes
/// its page inside the sheet through the shared router; Close dismisses Settings.
/// </summary>
public sealed class SettingsRootViewController(SettingOptionsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<SettingOptionsPageViewModel>(viewModel, dispatcher, logger), IRecipient<LanguageChanged>, IRecipient<WinoIntelligenceEntitlementChanged>
{
    private readonly UITableView _table = new(default, UITableViewStyle.InsetGrouped) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly RootSource _source = new();
    private UISearchController? _search;
    // The search controller keeps its results updater weakly.
    private SearchUpdater? _searchUpdater;
    private bool? _lastCanAccessIntelligence;

    public override void LoadView()
    {
        Title = Translator.MenuSettings;
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var zone = new WinoZoneView();
        root.AddSubview(zone);
        var guide = root.SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            zone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);

        _table.BackgroundColor = UIColor.Clear;
        _table.RegisterClassForCellReuse(typeof(UITableViewCell), RootSource.CellId);
        // Inset-grouped density: rows size from their 44pt content, section spacing comes from the header heights.
        _table.RowHeight = UITableView.AutomaticDimension;
        _table.EstimatedRowHeight = (nfloat)RootSource.RowHeight;
        _table.SectionHeaderTopPadding = 0;
        _table.EstimatedSectionHeaderHeight = (nfloat)RootSource.TitledHeaderHeight;
        _table.SectionFooterHeight = 0;
        _table.EstimatedSectionFooterHeight = 0;
        // Small side margins inside the zone; the inset-grouped sections follow the table's layout margins.
        _table.PreservesSuperviewLayoutMargins = false;
        _table.InsetsLayoutMarginsFromSafeArea = false;
        _table.DirectionalLayoutMargins = new NSDirectionalEdgeInsets(0, (nfloat)RootSource.SideMargin, 0, (nfloat)RootSource.SideMargin);
        _table.SeparatorInsetReference = UITableViewSeparatorInsetReference.CellEdges;
        _source.Selected = entry => { if (entry.Page is { } page) Navigate(page); };
        _table.Source = _source;
        WinoLayout.Fill(_table, zone.ContentView);
        View = root;

        _search = new UISearchController((UIViewController?)null) { ObscuresBackgroundDuringPresentation = false };
        _search.SearchBar.Placeholder = Translator.SearchBarPlaceholder;
        _searchUpdater = new SearchUpdater(query =>
        {
            _source.Query = query;
            _table.ReloadData();
        });
        _search.SearchResultsUpdater = _searchUpdater;
        NavigationItem.SearchController = _search;
        // Inline under the compact title, never the floating bottom search of iOS 26 that overlaps the last rows.
        NavigationItem.PreferredSearchBarPlacement = UINavigationItemSearchBarPlacement.Stacked;
        if (OperatingSystem.IsIOSVersionAtLeast(26)) NavigationItem.SearchBarPlacementAllowsToolbarIntegration = false;
        NavigationItem.HidesSearchBarWhenScrolling = true;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;

        Bind(nameof(ViewModel.AccountSummaryText), vm => vm.AccountSummaryText, summary =>
        {
            _source.ManageAccountsDetail = summary;
            _table.ReloadData();
        });
        Reload();

        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceEntitlementChanged>(this);
        Bindings.Own(new ActionDisposable(() => WeakReferenceMessenger.Default.UnregisterAll(this)));
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (NavigationController is { } navigation)
        {
            navigation.NavigationBar.PrefersLargeTitles = false;
            SettingsSheetChrome.Install(navigation);
        }
        if (_table.IndexPathForSelectedRow is { } selected) _table.DeselectRow(selected, animated);
    }

    private void Reload()
    {
        _source.Sections = SettingsCatalog.GetSections();
        _lastCanAccessIntelligence = SettingsCatalog.CanAccessIntelligence;
        _table.ReloadData();
    }

    private static void Navigate(WinoPage page)
        => WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(SettingsCatalog.Title(page), page));

    public void Receive(LanguageChanged message) => _ = Dispatcher.ExecuteOnUIThread(() =>
    {
        if (!IsActive) return;
        Title = Translator.MenuSettings;
        Reload();
    });

    /// <summary>Only an access change affects the list (every snapshot refresh republishes the entitlement).</summary>
    public void Receive(WinoIntelligenceEntitlementChanged message) => _ = Dispatcher.ExecuteOnUIThread(() =>
    {
        if (!IsActive || _lastCanAccessIntelligence == message.Entitlement.CanAccessSurfaces) return;
        Reload();
    });

    private sealed class SearchUpdater(Action<string?> changed) : NSObject, IUISearchResultsUpdating
    {
        public void UpdateSearchResultsForSearchController(UISearchController searchController) => changed(searchController.SearchBar.Text);
    }

    private sealed class RootSource : UITableViewSource
    {
        public const string CellId = "settings-root";
        public const double RowHeight = 44;
        public const double SideMargin = 10;
        /// <summary>Space above a group without a title (the first group, search results).</summary>
        public const double UntitledHeaderHeight = 12;
        /// <summary>A titled header: about 35pt between groups with the 13pt title sitting at its foot.</summary>
        public const double TitledHeaderHeight = 38;
        private const double GlyphSize = 22;
        /// <summary>Leading padding plus the glyph column and its gap: separators start at the row text.</summary>
        private const double TextInset = 14 + 28 + 12;

        public List<SettingsCatalogSection> Sections { get; set; } = [];
        public string? Query { get; set; }
        public string? ManageAccountsDetail { get; set; }
        public Action<SettingsCatalogEntry>? Selected { get; set; }

        private bool IsSearching => !string.IsNullOrWhiteSpace(Query);

        private List<SettingsCatalogSection> Visible
            => IsSearching ? [new SettingsCatalogSection(null, SettingsCatalog.Search(Query ?? string.Empty))] : Sections;

        public override nint NumberOfSections(UITableView tableView) => Visible.Count;

        public override nint RowsInSection(UITableView tableView, nint section) => Visible[(int)section].Entries.Count;

        public override nfloat GetHeightForHeader(UITableView tableView, nint section)
            => (nfloat)(string.IsNullOrEmpty(Visible[(int)section].Title) ? UntitledHeaderHeight : UITableView.AutomaticDimension);

        /// <summary>
        /// 44pt rows (iOS 26 cells otherwise grow past the inset-grouped density). Search results with a description,
        /// a wrapped value and accessibility text sizes size from their content.
        /// </summary>
        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
        {
            var entry = Visible[indexPath.Section].Entries[indexPath.Row];
            var fixedHeight = !IsSearching
                && UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body, tableView.TraitCollection).PointSize <= 23
                && (entry.Page != WinoPage.ManageAccountsPage || string.IsNullOrWhiteSpace(ManageAccountsDetail));
            return fixedHeight ? (nfloat)RowHeight : UITableView.AutomaticDimension;
        }

        public override nfloat GetHeightForFooter(UITableView tableView, nint section) => 0;

        public override UIView? GetViewForFooter(UITableView tableView, nint section) => null;

        /// <summary>The 13pt secondary section title at the foot of the gap between groups, aligned with the row text.</summary>
        public override UIView? GetViewForHeader(UITableView tableView, nint section)
        {
            var title = Visible[(int)section].Title;
            if (string.IsNullOrEmpty(title)) return null;
            var label = WinoStyle.Label(title, WinoSettingsStyle.GroupHeader, WinoStyle.SecondaryText, 0);
            label.AccessibilityTraits = UIAccessibilityTrait.Header;
            var header = new UIView();
            // The table's layout margins put the groups at SideMargin; the title aligns with the rows' 14pt padding.
            WinoLayout.Fill(label, header, TitledHeaderHeight - 22, SideMargin + 14, 6, SideMargin + 14);
            return header;
        }

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var entry = Visible[indexPath.Section].Entries[indexPath.Row];
            var cell = tableView.DequeueReusableCell(CellId, indexPath);
            // A search result explains itself with its description under the title; otherwise a short value sits
            // before the chevron (the account summary on Manage accounts).
            var content = IsSearching ? UIListContentConfiguration.SubtitleCellConfiguration : UIListContentConfiguration.ValueCellConfiguration;
            content.Text = entry.Title;
            content.TextProperties.Font = WinoStyle.Scaled(16);
            content.TextProperties.Color = WinoStyle.PrimaryText;
            content.SecondaryText = entry.Page == WinoPage.ManageAccountsPage && !string.IsNullOrWhiteSpace(ManageAccountsDetail)
                ? ManageAccountsDetail
                : IsSearching ? entry.Description : null;
            content.SecondaryTextProperties.Font = WinoStyle.Scaled(IsSearching ? 13 : 15);
            content.SecondaryTextProperties.Color = WinoStyle.SecondaryText;
            content.SecondaryTextProperties.NumberOfLines = IsSearching ? 2 : 1;
            content.PrefersSideBySideTextAndSecondaryText = !IsSearching;
            content.Image = WinoIcons.Image(entry.Icon, GlyphSize, WinoStyle.Accent);
            content.ImageProperties.ReservedLayoutSize = new CoreGraphics.CGSize(28, GlyphSize);
            content.ImageProperties.MaximumSize = new CoreGraphics.CGSize(GlyphSize, GlyphSize);
            content.ImageToTextPadding = 12;
            content.DirectionalLayoutMargins = new NSDirectionalEdgeInsets(IsSearching ? 8 : 11, 14, IsSearching ? 8 : 11, 12);
            cell.ContentConfiguration = content;
            cell.BackgroundColor = WinoSettingsStyle.CardFill;
            cell.SeparatorInset = new UIEdgeInsets(0, (nfloat)TextInset, 0, 0);
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            var entry = Visible[indexPath.Section].Entries[indexPath.Row];
            Selected?.Invoke(entry);
        }
    }
}
