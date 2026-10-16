using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// Search (Windows MailListPage search, Mac MailListPageViewController.Search): a search controller that the bar's Search
/// button attaches and activates (no persistent search bar under the compact title, so the list starts right under the
/// bar; it detaches again once the search is cancelled), whose scope bar picks Local or Online, active filters as removable tokens in the search field, suggestions that
/// turn the typed text into a From or Subject token or add the Unread, attachment and flag filters, and a row above the
/// results with the folder scope, the result status and the filter editor sheet. Searches run through the shared
/// criteria factory and folder resolver, as on the Mac.
/// </summary>
public sealed partial class MailListPageViewController
{
    private static readonly MailSearchScope[] Scopes = [MailSearchScope.CurrentFolder, MailSearchScope.Subfolders, MailSearchScope.AllFolders];
    private static readonly string[] ScopeTitles = [Translator.SearchBar_CurrentFolder, Translator.SearchBar_ScopeSubfolders, Translator.SearchBar_AllFolders];

    private readonly UISearchController _search = new();
    private readonly UIView _scopeBarRow = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIButton _scopeButton = new(UIButtonType.System) { ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _searchStatus = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly UIButton _filtersButton = new(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
    private SearchHandler? _searchHandler;
    private string _searchText = string.Empty;
    private bool _applyingTokens;

    private UIView ScopeBarRow => _scopeBarRow;

    private bool IsOnlineSearch => _search.SearchBar.SelectedScopeButtonIndex == 1;

    private void BuildSearch()
    {
        _searchHandler = new SearchHandler(this);
        _search.ObscuresBackgroundDuringPresentation = false;
        _search.SearchResultsUpdater = _searchHandler;
        var bar = _search.SearchBar;
        bar.Placeholder = Translator.SearchBarPlaceholder;
        bar.ScopeButtonTitles = [Translator.SettingsAppPreferences_SearchMode_Local, Translator.SettingsAppPreferences_SearchMode_Online];
        bar.SelectedScopeButtonIndex = 0;
        bar.Delegate = _searchHandler;
        bar.SearchTextField.AllowsDeletingTokens = true;
        bar.SearchTextField.TokenBackgroundColor = WinoStyle.Accent;
        _search.Delegate = _searchHandler;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        DefinesPresentationContext = true;

        var scopeConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        scopeConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        scopeConfiguration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        _scopeButton.Configuration = scopeConfiguration;
        WinoAccessibility.Label(_scopeButton, Translator.SearchBar_SearchIn);
        WinoLayout.Priorities(_scopeButton, UILayoutConstraintAxis.Horizontal, 250, 300);
        WinoLayout.Priorities(_searchStatus, UILayoutConstraintAxis.Horizontal, 1, 200);
        var filtersConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        filtersConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        filtersConfiguration.Image = WinoIcons.TemplateImage(WinoIconGlyph.Filter, 12);
        filtersConfiguration.ImagePadding = 4;
        _filtersButton.Configuration = filtersConfiguration;
        _filtersButton.PrimaryActionTriggered += (_, _) => ShowSearchFilters();
        WinoLayout.Priorities(_filtersButton, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        var row = WinoLayout.Insets(WinoLayout.HStack(8, _scopeButton, _searchStatus, _filtersButton), 6, 10, 6, 10);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(row, _scopeBarRow);
        UpdateScopeMenu();
    }

    private void BindSearch()
    {
        Bind(nameof(ViewModel.IsInSearchMode), vm => vm.IsInSearchMode, _ => UpdateScopeBar());
        Bind(nameof(ViewModel.SearchFilters), vm => vm.SearchFilters, _ => { UpdateScopeBar(); ApplyTokens(); });
        Bind(nameof(ViewModel.SearchScope), vm => vm.SearchScope, _ => UpdateScopeMenu());
        Bind(nameof(ViewModel.AreSearchResultsOnline), vm => vm.AreSearchResultsOnline, _ => UpdateScopeBar());
        Bind(nameof(ViewModel.IsEmpty), vm => vm.IsEmpty, _ => UpdateScopeBar());
    }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(_searchText) || ViewModel.IsInSearchMode || ViewModel.ActiveSearchFilterCount > 0;

    /// <summary>Puts the search controller in the bar (it is only there while a search is open).</summary>
    private void AttachSearch()
    {
        if (NavigationItem.SearchController is null) NavigationItem.SearchController = _search;
    }

    /// <summary>The bar's Search button: attaches the search controller and focuses its field.</summary>
    private void ShowSearch()
    {
        if (_released || _selectionMode) return;
        AttachSearch();
        // Activate on the next turn, after the bar has taken the search controller.
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_released) return;
            _search.Active = true;
            _search.SearchBar.BecomeFirstResponder();
        });
    }

    /// <summary>Removes the search controller from the bar once no search is open, giving the list its height back.</summary>
    private void DetachSearchIfIdle()
    {
        if (_released || _search.Active || IsSearchActive || NavigationItem.SearchController is null) return;
        NavigationItem.SearchController = null;
    }

    private void UpdateScopeBar()
    {
        _scopeBarRow.Hidden = !IsSearchActive;
        if (_scopeBarRow.Hidden) return;
        _searchStatus.Text = ViewModel.IsInSearchMode
            ? $"{(ViewModel.AreSearchResultsOnline ? Translator.SettingsAppPreferences_SearchMode_Online : Translator.SettingsAppPreferences_SearchMode_Local)} · {ViewModel.MailCollection.Count}"
            : string.Empty;
        if (_filtersButton.Configuration is { } configuration)
        {
            configuration.Title = WinoControlText.WithCount(Translator.SearchBar_Filters, ViewModel.ActiveSearchFilterCount);
            _filtersButton.Configuration = configuration;
        }
    }

    private void UpdateScopeMenu()
    {
        int selected = Array.IndexOf(Scopes, ViewModel.SearchScope);
        var actions = Scopes.Select((scope, index) => (UIMenuElement)PhoneBarItems.Action(ScopeTitles[index], WinoIconGlyph.None, () =>
        {
            if (ViewModel.SearchScope == scope) return;
            ViewModel.SearchScope = scope;
            Observe(RunSearchOrCloseAsync(_searchText));
        }, on: index == selected)).ToArray();
        _scopeButton.Menu = UIMenu.Create(actions);
        if (_scopeButton.Configuration is { } configuration)
        {
            configuration.Title = selected >= 0 ? ScopeTitles[selected] : ScopeTitles[0];
            _scopeButton.Configuration = configuration;
        }
    }

    // ---- Tokens (the active filters in the search field) ----

    /// <summary>Shows each active filter as a token; removing a token removes its filter.</summary>
    private void ApplyTokens()
    {
        var field = _search.SearchBar.SearchTextField;
        var chips = MailSearchFilterChip.From(ViewModel.SearchFilters ?? MailSearchFilters.Empty);
        var current = (field.Tokens ?? []).Select(static token => token.RepresentedObject?.ToString() ?? string.Empty).ToArray();
        var wanted = chips.Select(static chip => chip.Kind + "|" + chip.Text).ToArray();
        if (current.SequenceEqual(wanted)) return;
        _applyingTokens = true;
        try
        {
            field.Tokens = chips.Select(chip =>
            {
                var token = UISearchToken.Create(WinoIcons.TemplateImage(TokenGlyph(chip.Kind), 14), chip.Text);
                token.RepresentedObject = new NSString(chip.Kind + "|" + chip.Text);
                return token;
            }).ToArray();
        }
        finally { _applyingTokens = false; }
    }

    private static WinoIconGlyph TokenGlyph(MailSearchFilterKind kind) => kind switch
    {
        MailSearchFilterKind.Sender => WinoIconGlyph.Person,
        MailSearchFilterKind.Subject => WinoIconGlyph.TextDescription,
        MailSearchFilterKind.Date => WinoIconGlyph.Calendar,
        MailSearchFilterKind.ReadStatus => WinoIconGlyph.MarkUnread,
        MailSearchFilterKind.Attachments => WinoIconGlyph.Attachment,
        _ => WinoIconGlyph.Flag
    };

    /// <summary>The user deleted tokens: drop their filters and search again.</summary>
    private void TokensMaybeRemoved()
    {
        if (_applyingTokens) return;
        var filters = ViewModel.SearchFilters ?? MailSearchFilters.Empty;
        var present = new HashSet<string>((_search.SearchBar.SearchTextField.Tokens ?? []).Select(static token => token.RepresentedObject?.ToString() ?? string.Empty), StringComparer.Ordinal);
        var next = filters;
        foreach (var chip in MailSearchFilterChip.From(filters))
            if (!present.Contains(chip.Kind + "|" + chip.Text)) next = MailSearchFilterChip.Remove(next, chip.Kind);
        if (next == filters) return;
        ViewModel.SearchFilters = next;
        Observe(RunSearchOrCloseAsync(_searchText));
    }

    // ---- Suggestions ----

    private void UpdateSuggestions()
    {
        if (!_search.Active)
        {
            _search.SearchSuggestions = [];
            return;
        }
        var filters = ViewModel.SearchFilters ?? MailSearchFilters.Empty;
        var text = _searchText.Trim();
        var suggestions = new List<IUISearchSuggestion>();
        if (text.Length > 0)
        {
            suggestions.Add(Suggestion($"{Translator.SearchBar_From}: {text}", "sender", WinoIconGlyph.Person));
            suggestions.Add(Suggestion($"{Translator.SearchBar_Subject}: {text}", "subject", WinoIconGlyph.TextDescription));
        }
        if (filters.ReadStatus != MailReadStatusFilter.Unread) suggestions.Add(Suggestion(Translator.SearchBar_Unread, "unread", WinoIconGlyph.MarkUnread));
        if (!filters.HasAttachments) suggestions.Add(Suggestion(Translator.SearchBar_HasAttachments, "attachments", WinoIconGlyph.Attachment));
        if (!filters.IsFlagged) suggestions.Add(Suggestion(Translator.SearchBar_Flagged, "flagged", WinoIconGlyph.Flag));
        suggestions.Add(Suggestion(Translator.MacOS_MailList_SearchFilters, "filters", WinoIconGlyph.Filter));
        _search.SearchSuggestions = suggestions.ToArray();
    }

    private static IUISearchSuggestion Suggestion(string title, string code, WinoIconGlyph glyph)
    {
        var item = UISearchSuggestionItem.Create(new NSString(title), null, WinoIcons.TemplateImage(glyph, 18));
        item.RepresentedObject = new NSString(code);
        return item;
    }

    private void SuggestionChosen(IUISearchSuggestion suggestion)
    {
        var filters = ViewModel.SearchFilters ?? MailSearchFilters.Empty;
        var text = _searchText.Trim();
        switch (suggestion.RepresentedObject?.ToString())
        {
            case "sender":
                ViewModel.SearchFilters = filters with { Sender = text };
                SetSearchText(string.Empty);
                break;
            case "subject":
                ViewModel.SearchFilters = filters with { Subject = text };
                SetSearchText(string.Empty);
                break;
            case "unread":
                ViewModel.SearchFilters = filters with { ReadStatus = MailReadStatusFilter.Unread };
                break;
            case "attachments":
                ViewModel.SearchFilters = filters with { HasAttachments = true };
                break;
            case "flagged":
                ViewModel.SearchFilters = filters with { IsFlagged = true };
                break;
            case "filters":
                ShowSearchFilters();
                return;
            default:
                return;
        }
        _search.SearchSuggestions = [];
        Observe(RunSearchOrCloseAsync(_searchText));
    }

    private void SetSearchText(string text)
    {
        _searchText = text;
        ViewModel.SearchQuery = text;
        _search.SearchBar.Text = text;
    }

    // ---- Running searches (the Mac flow) ----

    private Task SearchTextChangedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        ViewModel.SearchQuery = _searchText;
        UpdateScopeBar();
        UpdateSuggestions();
        if (!string.IsNullOrWhiteSpace(_searchText)) return Task.CompletedTask;
        // Clearing the text keeps a filtered search on screen; it now runs on the filters alone.
        ViewModel.IsOnlineSearchButtonVisible = false;
        return RunSearchOrCloseAsync(string.Empty);
    }

    private Task SearchSubmittedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        _search.SearchSuggestions = [];
        UpdateScopeBar();
        return RunSearchOrCloseAsync(_searchText);
    }

    private async Task SearchClearedAsync()
    {
        _searchText = string.Empty;
        ViewModel.SearchFilters = MailSearchFilters.Empty;
        ViewModel.IsOnlineSearchButtonVisible = false;
        ViewModel.SetSearchCriteria(MailSearchCriteria.Empty, Array.Empty<IMailItemFolder>());
        await ViewModel.PerformSearchAsync();
        OnUI(() =>
        {
            _search.SearchBar.SelectedScopeButtonIndex = 0;
            UpdateScopeBar();
            DetachSearchIfIdle();
        });
    }

    private async Task RunOnlineSearchAsync()
    {
        await Dispatcher.ExecuteOnUIThread(() => _search.SearchBar.SelectedScopeButtonIndex = 1);
        await RunSearchOrCloseAsync(_searchText);
    }

    /// <summary>Searches again, or returns to the folder when neither a query nor a filter is left.</summary>
    private async Task RunSearchOrCloseAsync(string query)
    {
        if (!string.IsNullOrWhiteSpace(query) || ViewModel.ActiveSearchFilterCount > 0)
        {
            await RunMailSearchAsync(query);
            return;
        }
        ViewModel.SetSearchCriteria(MailSearchCriteria.Empty, Array.Empty<IMailItemFolder>());
        await ViewModel.PerformSearchAsync();
        OnUI(UpdateScopeBar);
    }

    private async Task RunMailSearchAsync(string query)
    {
        bool online = false;
        await Dispatcher.ExecuteOnUIThread(() => online = IsOnlineSearch);
        var scope = ViewModel.SearchScope;
        var activeFolders = ViewModel.ActiveFolder?.HandlingFolders.ToArray() ?? [];
        var folders = await MailSearchFolderResolver.ResolveAsync(scope, activeFolders,
            async accountId => (IReadOnlyList<IMailItemFolder>)(await _folderService.GetFoldersAsync(accountId).ConfigureAwait(false)).Cast<IMailItemFolder>().ToList())
            .ConfigureAwait(false);
        var criteria = MailSearchCriteriaFactory.Create(query, online ? SearchBarReach.IncludeServer : SearchBarReach.DownloadedOnly,
            scope, ViewModel.SearchFilters, DateTime.Now,
            folders.Select(static folder => folder.Id).ToArray(),
            folders.Select(static folder => folder.MailAccountId).Distinct().ToArray());

        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            ViewModel.SetSearchCriteria(criteria, folders);
            if (ViewModel.PerformSearchCommand.CanExecute(null)) ViewModel.PerformSearchCommand.Execute(null);
            UpdateScopeBar();
        });
    }

    // ---- Filter editor sheet (Windows SearchFilterFlyout) ----

    private void ShowSearchFilters()
    {
        if (_released) return;
        DismissFilterSheet();
        ViewModel.SearchFilterEditor.Load(ViewModel.SearchScope, _searchText, ViewModel.SearchFilters ?? MailSearchFilters.Empty, !IsOnlineSearch);
        var editor = new MailListFilterSheetViewController(ViewModel.SearchFilterEditor, () =>
        {
            DismissFilterSheet();
            Observe(ApplySearchFiltersAsync());
        }, DismissFilterSheet);
        _filterSheet = _host.PresentSheet(editor, PhoneSheetSize.Large, () => _filterSheet = null);
    }

    private void DismissFilterSheet()
    {
        if (_filterSheet is not { } sheet) return;
        _filterSheet = null;
        _ = _host.DismissSheetAsync(sheet);
    }

    /// <summary>Windows ApplySearchFilterEditorAsync: the keywords become the search text.</summary>
    private Task ApplySearchFiltersAsync()
    {
        var editor = ViewModel.SearchFilterEditor;
        var keywords = (editor.Keywords ?? string.Empty).Trim();
        ViewModel.SearchScope = editor.Scope;
        ViewModel.SearchFilters = editor.ToFilters();
        AttachSearch();
        if (!_search.Active) _search.Active = true;
        SetSearchText(keywords);
        UpdateScopeBar();
        return RunSearchOrCloseAsync(keywords);
    }

    /// <summary>The search bar's delegate and the results updater (tokens and suggestions).</summary>
    private sealed class SearchHandler(MailListPageViewController owner) : UISearchResultsUpdating, IUISearchBarDelegate, IUISearchControllerDelegate
    {
        [Export("didDismissSearchController:")]
        public void DidDismissSearchController(UISearchController searchController) => owner.DetachSearchIfIdle();

        public override void UpdateSearchResultsForSearchController(UISearchController searchController)
        {
            owner.TokensMaybeRemoved();
            owner.UpdateSuggestions();
        }

        public override void UpdateSearchResults(UISearchController searchController, IUISearchSuggestion searchSuggestion)
            => owner.SuggestionChosen(searchSuggestion);

        [Export("searchBar:textDidChange:")]
        public void TextChanged(UISearchBar searchBar, string searchText) => owner.Observe(owner.SearchTextChangedAsync(searchText));

        [Export("searchBarSearchButtonClicked:")]
        public void SearchButtonClicked(UISearchBar searchBar)
        {
            searchBar.ResignFirstResponder();
            owner.Observe(owner.SearchSubmittedAsync(searchBar.Text ?? string.Empty));
        }

        [Export("searchBarCancelButtonClicked:")]
        public void CancelButtonClicked(UISearchBar searchBar) => owner.Observe(owner.SearchClearedAsync());

        [Export("searchBar:selectedScopeButtonIndexDidChange:")]
        public void SelectedScopeButtonIndexChanged(UISearchBar searchBar, nint selectedScope)
            => owner.Observe(owner.RunSearchOrCloseAsync(owner._searchText));
    }
}
