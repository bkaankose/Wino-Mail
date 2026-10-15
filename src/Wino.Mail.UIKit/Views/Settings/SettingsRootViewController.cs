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
using Wino.Presentation.UIKit;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// The Settings sheet's first page on iPhone and iPad (the Mac Settings sidebar and the Windows settings pane): a grouped
/// list in the Windows groups, names and order inside a Wino zone, with settings search in the navigation bar. A row
/// pushes its page inside the sheet through the shared router; Done closes Settings.
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
        NavigationItem.HidesSearchBarWhenScrolling = true;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;

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
            navigation.NavigationBar.PrefersLargeTitles = true;
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

        public List<SettingsCatalogSection> Sections { get; set; } = [];
        public string? Query { get; set; }
        public string? ManageAccountsDetail { get; set; }
        public Action<SettingsCatalogEntry>? Selected { get; set; }

        private bool IsSearching => !string.IsNullOrWhiteSpace(Query);

        private List<SettingsCatalogSection> Visible
            => IsSearching ? [new SettingsCatalogSection(null, SettingsCatalog.Search(Query ?? string.Empty))] : Sections;

        public override nint NumberOfSections(UITableView tableView) => Visible.Count;

        public override nint RowsInSection(UITableView tableView, nint section) => Visible[(int)section].Entries.Count;

        public override string? TitleForHeader(UITableView tableView, nint section) => Visible[(int)section].Title;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var entry = Visible[indexPath.Section].Entries[indexPath.Row];
            var cell = tableView.DequeueReusableCell(CellId, indexPath);
            var content = UIListContentConfiguration.SubtitleCellConfiguration;
            content.Text = entry.Title;
            content.SecondaryText = entry.Page == WinoPage.ManageAccountsPage && !string.IsNullOrWhiteSpace(ManageAccountsDetail)
                ? ManageAccountsDetail
                : IsSearching ? entry.Description : null;
            content.SecondaryTextProperties.Color = WinoStyle.SecondaryText;
            content.Image = WinoIcons.Image(entry.Icon, 20, WinoStyle.Accent);
            content.ImageProperties.ReservedLayoutSize = new CoreGraphics.CGSize(28, 28);
            content.ImageProperties.MaximumSize = new CoreGraphics.CGSize(22, 22);
            cell.ContentConfiguration = content;
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
