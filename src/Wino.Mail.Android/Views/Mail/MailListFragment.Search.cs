using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Google.Android.Material.Chip;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.Android;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// Search (canvas board Search; Windows MailListPage search, the iOS search controller): the app bar turns into a search
/// field with the On device / Online scope, filter chips for attachments, unread and flagged mail, From and Subject chips
/// that turn the typed text into a sender or subject filter, and the folder scope. Results replace the folder's rows in
/// the same list; the header shows the result status and Try online search appears under local results. Searches run
/// through the shared criteria factory and folder resolver, as on the Mac and iOS.
/// </summary>
public sealed partial class MailListFragment
{
    private static readonly MailSearchScope[] Scopes = [MailSearchScope.CurrentFolder, MailSearchScope.Subfolders, MailSearchScope.AllFolders];

    private View? _searchPanel;
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
        _searchField = view.FindViewById<EditText>(Resource.Id.mail_search_field);
        _searchChips = view.FindViewById<ChipGroup>(Resource.Id.mail_search_chips);
        _searchStatus = view.FindViewById<TextView>(Resource.Id.mail_search_status);
        _headerSpace = view.FindViewById(Resource.Id.mail_header_space);
        if (_searchField is null) return;

        // Back closes search first (Material 3 search), then leaves the page as before.
        if (Activity is { } activity)
        {
            _searchBack = new SearchBackCallback(this);
            activity.OnBackPressedDispatcher.AddCallback(ViewLifecycleOwner, _searchBack);
        }
        _searchField.Hint = Translator.SearchBarPlaceholder;
        _searchField.TextChanged += (_, _) => Observe(SearchTextChangedAsync(_searchField.Text ?? string.Empty));
        _searchField.EditorAction += (_, args) =>
        {
            args.Handled = args.ActionId == ImeAction.Search;
            if (!args.Handled) return;
            HideKeyboard();
            Observe(RunSearchOrCloseAsync(_searchText));
        };
        if (view.FindViewById<ImageButton>(Resource.Id.mail_search_close) is { } close)
        {
            close.ContentDescription = Translator.Buttons_Back;
            close.Click += (_, _) => CloseSearch();
        }
        if (view.FindViewById<ImageButton>(Resource.Id.mail_search_clear) is { } clear)
        {
            clear.ContentDescription = Translator.SearchBar_ClearAllFilters;
            clear.Click += (_, _) => _searchField.Text = string.Empty;
        }

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

    private void OpenSearch() => ShowSearchPanel(focus: true);

    private void ShowSearchPanel(bool focus)
    {
        if (_searchPanel is null || _searchField is null) return;
        _searchPanel.Visibility = ViewStates.Visible;
        if (_toolbar is not null) _toolbar.Visibility = ViewStates.Gone;
        Chrome?.SuppressFab(true);
        if (_searchBack is not null) _searchBack.Enabled = true;
        if (_searchField.Text != _searchText) _searchField.Text = _searchText;
        BuildSearchChips();
        UpdateSearchStatus();
        if (!focus) return;
        _searchField.RequestFocus();
        (Context?.GetSystemService(Context.InputMethodService) as InputMethodManager)?.ShowSoftInput(_searchField, ShowFlags.Implicit);
    }

    /// <summary>Back to the folder: the query and filters go, the folder's rows return.</summary>
    private void CloseSearch()
    {
        HideKeyboard();
        if (_searchPanel is not null) _searchPanel.Visibility = ViewStates.Gone;
        if (_toolbar is not null) _toolbar.Visibility = ViewStates.Visible;
        Chrome?.SuppressFab(false);
        if (_searchBack is not null) _searchBack.Enabled = false;
        _online = false;
        _searchText = string.Empty;
        if (_searchField is not null && !string.IsNullOrEmpty(_searchField.Text)) _searchField.Text = string.Empty;
        Observe(SearchClearedAsync());
    }

    private void HideKeyboard()
    {
        if (_searchField is not null)
            (Context?.GetSystemService(Context.InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(_searchField.WindowToken, HideSoftInputFlags.None);
    }

    private SearchBackCallback? _searchBack;

    private sealed class SearchBackCallback(MailListFragment owner) : AndroidX.Activity.OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed() => owner.CloseSearch();
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

            _localChip = Choice(context, Translator.SettingsAppPreferences_SearchMode_Local, !_online, () => SetOnline(false));
            _onlineChip = Choice(context, Translator.SettingsAppPreferences_SearchMode_Online, _online, () => SetOnline(true));

            Toggle(context, Translator.SearchBar_HasAttachments, WinoIconGlyph.Attachment, filters.HasAttachments, on => filters with { HasAttachments = on });
            Toggle(context, Translator.SearchBar_Unread, WinoIconGlyph.MarkUnread, filters.ReadStatus == MailReadStatusFilter.Unread,
                on => filters with { ReadStatus = on ? MailReadStatusFilter.Unread : MailReadStatusFilter.All });
            Toggle(context, Translator.SearchBar_Flagged, WinoIconGlyph.Flag, filters.IsFlagged, on => filters with { IsFlagged = on });
            // From and Subject turn the typed text into a sender or subject filter (the iOS suggestions).
            Toggle(context, string.IsNullOrEmpty(filters.Sender) ? Translator.SearchBar_From : $"{Translator.SearchBar_From}: {filters.Sender}", WinoIconGlyph.Person,
                !string.IsNullOrEmpty(filters.Sender), on => filters with { Sender = on ? TakeSearchText() ?? string.Empty : string.Empty });
            Toggle(context, string.IsNullOrEmpty(filters.Subject) ? Translator.SearchBar_Subject : $"{Translator.SearchBar_Subject}: {filters.Subject}", WinoIconGlyph.TextDescription,
                !string.IsNullOrEmpty(filters.Subject), on => filters with { Subject = on ? TakeSearchText() ?? string.Empty : string.Empty });

            int scopeIndex = Math.Max(0, Array.IndexOf(Scopes, viewModel.SearchScope));
            var scope = WinoChips.Assist(context, ScopeTitle(Scopes[scopeIndex]), WinoIconGlyph.Folder);
            scope.ContentDescription = Translator.SearchBar_SearchIn;
            scope.Click += (_, _) => ShowScopeMenu(scope);
            _searchChips.AddView(scope);
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

    private void Toggle(Context context, string text, WinoIconGlyph glyph, bool isChecked, Func<bool, MailSearchFilters> next)
    {
        var chip = WinoChips.Filter(context, text, isChecked);
        chip.ChipIcon = WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small), WinoColors.SecondaryText(context));
        chip.ChipIconVisible = !isChecked;
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
        if (_searchField is not null) _searchField.Text = string.Empty;
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
        var popup = new PopupMenu(context, anchor);
        for (int index = 0; index < Scopes.Length; index++)
        {
            var item = popup.Menu.Add(0, index, index, ScopeTitle(Scopes[index]));
            item?.SetCheckable(true);
            item?.SetChecked(viewModel.SearchScope == Scopes[index]);
        }
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item is not { } item || item.ItemId < 0 || item.ItemId >= Scopes.Length) return;
            var scope = Scopes[item.ItemId];
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
        viewModel.SearchFilters = MailSearchFilters.Empty;
        viewModel.SearchQuery = string.Empty;
        viewModel.IsOnlineSearchButtonVisible = false;
        viewModel.SetSearchCriteria(MailSearchCriteria.Empty, Array.Empty<IMailItemFolder>());
        await viewModel.PerformSearchAsync();
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
        if (MainApplication.Services?.GetService<IFolderService>() is not { } folderService) return;
        bool online = _online;
        var scope = viewModel.SearchScope;
        var activeFolders = viewModel.ActiveFolder?.HandlingFolders.ToArray() ?? [];
        var folders = await MailSearchFolderResolver.ResolveAsync(scope, activeFolders,
            async accountId => (IReadOnlyList<IMailItemFolder>)(await folderService.GetFoldersAsync(accountId).ConfigureAwait(false)).Cast<IMailItemFolder>().ToList())
            .ConfigureAwait(false);
        var criteria = MailSearchCriteriaFactory.Create(query, online ? SearchBarReach.IncludeServer : SearchBarReach.DownloadedOnly,
            scope, viewModel.SearchFilters, DateTime.Now,
            folders.Select(static folder => folder.Id).ToArray(),
            folders.Select(static folder => folder.MailAccountId).Distinct().ToArray());
        var dispatcher = Entry?.Dispatcher;
        if (dispatcher is null) return;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            viewModel.SetSearchCriteria(criteria, folders);
            if (viewModel.PerformSearchCommand.CanExecute(null)) viewModel.PerformSearchCommand.Execute(null);
            UpdateSearchStatus();
        });
    }
}
