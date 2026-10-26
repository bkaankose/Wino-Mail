using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Google.Android.Material.Chip;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.Android;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// Search (canvas board Search; Windows MailListPage search, the iOS search controller): typing in the docked search bar
/// (<see cref="Shell.DockedSearchBar"/>) searches; the chip row under it holds the On device / Online scope, filter chips for attachments, unread and flagged mail, From and Subject chips
/// that turn the typed text into a sender or subject filter, and the folder scope. Results replace the folder's rows in
/// the same list; the header shows the result status and Try online search appears under local results. Searches run
/// through the shared <see cref="MailListPageViewModel.SearchAsync"/>, as on the Mac.
/// </summary>
public sealed partial class MailListFragment
{
    private View? _searchPanel;
    private CancellationTokenSource _searchCancellation = new();
    private EditText? _searchField;
    private ChipGroup? _searchChips;
    private TextView? _searchStatus;
    private View? _headerSpace;
    private Chip? _localChip;
    private Chip? _onlineChip;
    private string _searchText = string.Empty;
    private bool _applyingSearchChips;
    private bool _online;

    private void BuildSearch(View view, Context context, MailListPageViewModel viewModel, IDispatcher dispatcher)
    {
        _searchPanel = view.FindViewById(Resource.Id.mail_search_panel);
        _searchChips = view.FindViewById<ChipGroup>(Resource.Id.mail_search_chips);
        _searchStatus = view.FindViewById<TextView>(Resource.Id.mail_search_status);
        _headerSpace = view.FindViewById(Resource.Id.mail_header_space);
        if (_searchBar is not { } bar) return;
        _searchField = bar.Field;
        var searchCancellation = _searchCancellation = new CancellationTokenSource();
        Bindings.Own(new ActionDisposable(searchCancellation.Cancel));

        // The bar's Back (button or gesture) leaves search; focusing its field enters it.
        bar.ActiveChanged += active =>
        {
            if (active) ShowSearchPanel(focus: false);
            else CloseSearch();
        };
        bar.QueryChanged += text => Observe(SearchTextChangedAsync(text));
        bar.Submitted += _ => Observe(RunSearchOrCloseAsync(_searchText));

        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsInSearchMode), vm => vm.IsInSearchMode, _ => UpdateSearchStatus());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.SearchFilters), vm => vm.SearchFilters, _ => { UpdateSearchStatus(); BuildSearchChips(); });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.SearchScope), vm => vm.SearchScope, _ => BuildSearchChips());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.AreSearchResultsOnline), vm => vm.AreSearchResultsOnline, _ => UpdateSearchStatus());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsEmpty), vm => vm.IsEmpty, _ => UpdateSearchStatus());

        // A search still running when the view was recreated (rotation) keeps its panel.
        if (viewModel.IsInSearchMode || viewModel.ActiveSearchFilterCount > 0 || !string.IsNullOrEmpty(viewModel.SearchQuery))
        {
            _searchText = viewModel.SearchQuery ?? string.Empty;
            ShowSearchPanel(focus: false);
        }
    }

    private void ShowSearchPanel(bool focus)
    {
        if (_searchPanel is null || _searchBar is not { } bar) return;
        _searchPanel.Visibility = ViewStates.Visible;
        Chrome?.SuppressFab(true);
        bar.Query = _searchText;
        if (!bar.IsActive || focus) bar.Activate(focus);
        BuildSearchChips();
        UpdateSearchStatus();
    }

    /// <summary>Back to the folder (the bar left search): the query and filters go, the folder's rows return.</summary>
    private void CloseSearch()
    {
        if (_closingSearch) return;
        _closingSearch = true;
        try { CloseSearchCore(); }
        finally { _closingSearch = false; }
    }

    private bool _closingSearch;

    private void CloseSearchCore()
    {
        if (_searchPanel is not null) _searchPanel.Visibility = ViewStates.Gone;
        Chrome?.SuppressFab(false);
        _online = false;
        _searchText = string.Empty;
        if (_searchBar is { IsActive: true } bar) bar.Deactivate();
        UpdateSearchStatus();
        Observe(SearchClearedAsync());
    }

    private bool IsSearchPanelOpen => _searchPanel?.Visibility == ViewStates.Visible;

    // ---- Chips ----

    private void BuildSearchChips()
    {
        if (_searchChips is null || ViewModel is not { } viewModel || View?.Context is not { } context) return;
        _applyingSearchChips = true;
        try
        {
            _searchChips.RemoveAllViews();
            var filters = viewModel.SearchFilters ?? MailSearchFilters.Empty;

            // Canvas Material pass Search: the folder scope dropdown leads the row; the chips carry text only.
            var scopes = MailListPageViewModel.SearchScopes;
            int scopeIndex = Math.Max(0, scopes.IndexOf(viewModel.SearchScope));
            var scope = WinoChips.Assist(context, ScopeTitle(scopes[scopeIndex]));
            scope.ContentDescription = Translator.SearchBar_SearchIn;
            scope.Click += (_, _) => ShowScopeMenu(scope);
            _searchChips.AddView(scope);

            _localChip = Choice(context, Translator.SettingsAppPreferences_SearchMode_Local, !_online, () => SetOnline(false));
            _onlineChip = Choice(context, Translator.SettingsAppPreferences_SearchMode_Online, _online, () => SetOnline(true));

            Toggle(context, Translator.SearchBar_HasAttachments, filters.HasAttachments, on => filters with { HasAttachments = on });
            Toggle(context, Translator.SearchBar_Unread, filters.ReadStatus == MailReadStatusFilter.Unread,
                on => filters with { ReadStatus = on ? MailReadStatusFilter.Unread : MailReadStatusFilter.All });
            Toggle(context, Translator.SearchBar_Flagged, filters.IsFlagged, on => filters with { IsFlagged = on });
            // From and Subject turn the typed text into a sender or subject filter (the iOS suggestions).
            Toggle(context, string.IsNullOrEmpty(filters.Sender) ? Translator.SearchBar_From : $"{Translator.SearchBar_From}: {filters.Sender}",
                !string.IsNullOrEmpty(filters.Sender), on => filters with { Sender = on ? TakeSearchText() ?? string.Empty : string.Empty });
            Toggle(context, string.IsNullOrEmpty(filters.Subject) ? Translator.SearchBar_Subject : $"{Translator.SearchBar_Subject}: {filters.Subject}",
                !string.IsNullOrEmpty(filters.Subject), on => filters with { Subject = on ? TakeSearchText() ?? string.Empty : string.Empty });
        }
        finally { _applyingSearchChips = false; }
    }

    private Chip Choice(Context context, string text, bool isChecked, Action selected)
    {
        var chip = WinoChips.Filter(context, text, isChecked);
        chip.CheckedChange += (_, args) =>
        {
            if (_applyingSearchChips) return;
            if (args.IsChecked) selected();
            else BuildSearchChips();
        };
        _searchChips?.AddView(chip);
        return chip;
    }

    private void Toggle(Context context, string text, bool isChecked, Func<bool, MailSearchFilters> next)
    {
        var chip = WinoChips.Filter(context, text, isChecked);
        chip.CheckedChange += (_, args) =>
        {
            if (_applyingSearchChips || ViewModel is not { } viewModel) return;
            viewModel.SearchFilters = next(args.IsChecked);
            Observe(RunSearchOrCloseAsync(_searchText));
        };
        _searchChips?.AddView(chip);
    }

    /// <summary>Moves the typed text into a filter: the field empties, as the iOS token suggestions do.</summary>
    private string? TakeSearchText()
    {
        var text = _searchText.Trim();
        if (text.Length == 0) return null;
        _searchText = string.Empty;
        if (ViewModel is { } viewModel) viewModel.SearchQuery = string.Empty;
        if (_searchBar is not null) _searchBar.Query = string.Empty;
        return text;
    }

    private void SetOnline(bool online)
    {
        if (_online == online) return;
        _online = online;
        BuildSearchChips();
        Observe(RunSearchOrCloseAsync(_searchText));
    }

    private void ShowScopeMenu(View anchor)
    {
        if (Context is not { } context || ViewModel is not { } viewModel) return;
        var scopes = MailListPageViewModel.SearchScopes;
        var popup = new PopupMenu(context, anchor);
        for (int index = 0; index < scopes.Length; index++)
        {
            var item = popup.Menu.Add(0, index, index, ScopeTitle(scopes[index]));
            item?.SetCheckable(true);
            item?.SetChecked(viewModel.SearchScope == scopes[index]);
        }
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item is not { } item || item.ItemId < 0 || item.ItemId >= scopes.Length) return;
            var scope = scopes[item.ItemId];
            if (viewModel.SearchScope == scope) return;
            viewModel.SearchScope = scope;
            Observe(RunSearchOrCloseAsync(_searchText));
        };
        popup.Show();
    }

    private static string ScopeTitle(MailSearchScope scope) => scope switch
    {
        MailSearchScope.Subfolders => Translator.SearchBar_ScopeSubfolders,
        MailSearchScope.AllFolders => Translator.SearchBar_AllFolders,
        _ => Translator.SearchBar_CurrentFolder
    };

    /// <summary>The header's status while searching ("Local · 4") in place of Focused/Other.</summary>
    private void UpdateSearchStatus()
    {
        if (_searchStatus is null || ViewModel is not { } viewModel) return;
        bool searching = IsSearchPanelOpen && viewModel.IsInSearchMode;
        _searchStatus.Visibility = searching ? ViewStates.Visible : ViewStates.Gone;
        if (_headerSpace is not null) _headerSpace.Visibility = searching ? ViewStates.Gone : ViewStates.Visible;
        if (_pivots is not null && searching) _pivots.Visibility = ViewStates.Gone;
        else if (!searching) UpdatePivots();
        UpdateHeaderVisibility();
        if (searching)
            _searchStatus.Text = $"{(viewModel.AreSearchResultsOnline ? Translator.SettingsAppPreferences_SearchMode_Online : Translator.SettingsAppPreferences_SearchMode_Local)} · {viewModel.MailCollection.Count}";
    }

    // ---- Running searches (the Mac and iOS flow) ----

    private Task SearchTextChangedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        if (ViewModel is not { } viewModel) return Task.CompletedTask;
        viewModel.SearchQuery = _searchText;
        if (!string.IsNullOrWhiteSpace(_searchText)) return Task.CompletedTask;
        // Clearing the text keeps a filtered search on screen; it now runs on the filters alone.
        viewModel.IsOnlineSearchButtonVisible = false;
        return IsSearchPanelOpen ? RunSearchOrCloseAsync(string.Empty) : Task.CompletedTask;
    }

    private async Task SearchClearedAsync()
    {
        if (ViewModel is not { } viewModel) return;
        viewModel.SearchQuery = string.Empty;
        await viewModel.ClearSearchAsync();
    }

    private Task RunOnlineSearchAsync()
    {
        _online = true;
        if (!IsSearchPanelOpen) ShowSearchPanel(focus: false);
        BuildSearchChips();
        return RunSearchOrCloseAsync(_searchText);
    }

    /// <summary>Searches again, or returns to the folder when neither a query nor a filter is left.</summary>
    private async Task RunSearchOrCloseAsync(string query)
    {
        if (ViewModel is not { } viewModel) return;
        if (!string.IsNullOrWhiteSpace(query) || viewModel.ActiveSearchFilterCount > 0)
        {
            await RunMailSearchAsync(viewModel, query);
            return;
        }
        viewModel.SetSearchCriteria(MailSearchCriteria.Empty, Array.Empty<IMailItemFolder>());
        await viewModel.PerformSearchAsync();
    }

    private async Task RunMailSearchAsync(MailListPageViewModel viewModel, string query)
    {
        await viewModel.SearchAsync(query, _online, _searchCancellation.Token);
        if (Entry?.Dispatcher is { } dispatcher) await dispatcher.ExecuteOnUIThread(UpdateSearchStatus);
    }
}
