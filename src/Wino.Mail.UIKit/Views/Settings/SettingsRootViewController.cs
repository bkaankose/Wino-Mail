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
/// The Settings sheet's first page on iPhone and iPad (the Mac Settings sidebar and the Windows settings pane), laid out
/// like the iOS Settings app: a large title that folds into the bar, the mail accounts card first (in the place and
/// shape of the Apple Account card) with the Wino Account row under it, then the Windows groups, names and order on
/// inset-grouped surfaces. Search sits in the bottom toolbar on iOS 26 and under the title before it. A row pushes its
/// page inside the sheet through the shared router; Close dismisses Settings.
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
        _table.BackgroundColor = WinoStyle.ZoneFill;
        _table.RegisterClassForCellReuse(typeof(UITableViewCell), RootSource.CellId);
        // Inset-grouped density: rows size from their 44pt content, section spacing comes from the header heights.
        _table.RowHeight = UITableView.AutomaticDimension;
        _table.EstimatedRowHeight = (nfloat)RootSource.RowHeight;
        _table.SectionHeaderTopPadding = 0;
        _table.EstimatedSectionHeaderHeight = (nfloat)RootSource.TitledHeaderHeight;
        _table.SectionFooterHeight = 0;
        _table.EstimatedSectionFooterHeight = 0;
        _table.SeparatorInsetReference = UITableViewSeparatorInsetReference.CellEdges;
        _source.Selected = entry => { if (entry.Page is { } page) Navigate(page); };
        _table.Source = _source;
        // The table is the page so the bars find it: the large title folds as it scrolls and the toolbar insets it.
        View = _table;

        _search = new UISearchController((UIViewController?)null) { ObscuresBackgroundDuringPresentation = false };
        _search.SearchBar.Placeholder = Translator.SearchBarPlaceholder;
        _searchUpdater = new SearchUpdater(query =>
        {
            if (_source.Query == query) return;
            _source.Query = query;
            _table.ReloadData();
            // The full list may have been scrolled far past the end of a short result list.
            _table.SetContentOffset(new CoreGraphics.CGPoint(0, -_table.AdjustedContentInset.Top), false);
        });
        _search.SearchResultsUpdater = _searchUpdater;
        NavigationItem.SearchController = _search;
        if (UsesToolbarSearch)
        {
            NavigationItem.PreferredSearchBarPlacement = UINavigationItemSearchBarPlacement.Integrated;
            NavigationItem.SearchBarPlacementAllowsToolbarIntegration = true;
            NavigationItem.HidesSearchBarWhenScrolling = false;
            ToolbarItems = [NavigationItem.SearchBarPlacementBarButtonItem];
        }
        else
        {
            NavigationItem.PreferredSearchBarPlacement = UINavigationItemSearchBarPlacement.Stacked;
            NavigationItem.HidesSearchBarWhenScrolling = true;
        }
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;

        // HasAccounts is announced after the account list is refilled.
        Bind(nameof(ViewModel.HasAccounts), vm => vm.HasAccounts, _ => _source.MailAccounts.Update(ViewModel.Accounts));
        Bind(nameof(ViewModel.IsWinoAccountSignedIn), vm => vm.IsWinoAccountSignedIn, _ => UpdateWinoAccountCard());
        Bind(nameof(ViewModel.WinoAccountDisplayName), vm => vm.WinoAccountDisplayName, _ => UpdateWinoAccountCard());
        Bind(nameof(ViewModel.WinoAccountAvatarPath), vm => vm.WinoAccountAvatarPath, _ => UpdateWinoAccountCard());
        Reload();

        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceEntitlementChanged>(this);
        Bindings.Own(new ActionDisposable(() => WeakReferenceMessenger.Default.UnregisterAll(this)));
    }

    /// <summary>The iOS 26 Settings app search: a field in the bottom toolbar of the first page.</summary>
    [System.Runtime.Versioning.SupportedOSPlatformGuard("ios26.0")]
    [System.Runtime.Versioning.SupportedOSPlatformGuard("maccatalyst26.0")]
    private static bool UsesToolbarSearch => OperatingSystem.IsIOSVersionAtLeast(26);

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (NavigationController is { } navigation)
        {
            navigation.NavigationBar.PrefersLargeTitles = true;
            SettingsSheetChrome.Install(navigation);
            if (UsesToolbarSearch) navigation.SetToolbarHidden(false, animated);
        }
        if (_table.IndexPathForSelectedRow is { } selected) _table.DeselectRow(selected, animated);
    }

    public override void ViewWillDisappear(bool animated)
    {
        base.ViewWillDisappear(animated);
        if (UsesToolbarSearch) NavigationController?.SetToolbarHidden(true, animated);
    }

    private void UpdateWinoAccountCard()
    {
        var vm = ViewModel;
        _source.WinoAccount.Update(vm.IsWinoAccountSignedIn, vm.WinoAccountDisplayName, vm.WinoAccountEmail, LoadPicture(vm.WinoAccountAvatarPath));
    }

    private UIImage? LoadPicture(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return UIImage.FromFile(path);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not load the Wino Account picture.");
        }
        return null;
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
        /// <summary>Space above a group without a title (the first group, search results).</summary>
        public const double UntitledHeaderHeight = 12;
        /// <summary>A titled header: about 35pt between groups with the 13pt title sitting at its foot.</summary>
        public const double TitledHeaderHeight = 38;
        private const double GlyphSize = 22;
        /// <summary>Leading padding plus the glyph column and its gap: separators start at the row text.</summary>
        private const double TextInset = 14 + 28 + 12;

        public List<SettingsCatalogSection> Sections { get; set; } = [];
        public MailAccountsCardCell MailAccounts { get; } = new();
        public WinoAccountRowCell WinoAccount { get; } = new();
        public string? Query { get; set; }
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
                && entry.Page != WinoPage.ManageAccountsPage;
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
            label.TranslatesAutoresizingMaskIntoConstraints = false;
            // The header follows the table's margins, where the inset-grouped sections start; the title aligns with the rows' 14pt padding.
            var header = new UIView { PreservesSuperviewLayoutMargins = true };
            header.AddSubview(label);
            var margins = header.LayoutMarginsGuide;
            NSLayoutConstraint.ActivateConstraints(
            [
                label.TopAnchor.ConstraintEqualTo(header.TopAnchor, (nfloat)(TitledHeaderHeight - 22)),
                label.BottomAnchor.ConstraintEqualTo(header.BottomAnchor, -6),
                label.LeadingAnchor.ConstraintEqualTo(margins.LeadingAnchor, 14),
                label.TrailingAnchor.ConstraintEqualTo(margins.TrailingAnchor, -14)
            ]);
            return header;
        }

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var entry = Visible[indexPath.Section].Entries[indexPath.Row];
            if (!IsSearching && entry.Page == WinoPage.WinoAccountManagementPage) return WinoAccount;
            if (!IsSearching && entry.Page == WinoPage.ManageAccountsPage) return MailAccounts;
            var cell = tableView.DequeueReusableCell(CellId, indexPath);
            // A search result explains itself with its description under the title.
            var content = IsSearching ? UIListContentConfiguration.SubtitleCellConfiguration : UIListContentConfiguration.ValueCellConfiguration;
            content.Text = entry.Title;
            content.TextProperties.Font = WinoStyle.Scaled(16);
            content.TextProperties.Color = WinoStyle.PrimaryText;
            content.SecondaryText = IsSearching ? entry.Description : null;
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
