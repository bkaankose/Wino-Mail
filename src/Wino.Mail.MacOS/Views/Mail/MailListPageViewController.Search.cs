using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Search;

namespace Wino.Mail.MacOS.Views.Mail;

public sealed partial class MailListPageViewController : IShellSearchTarget
{
    private static readonly MailSearchScope[] Scopes = [MailSearchScope.CurrentFolder, MailSearchScope.Subfolders, MailSearchScope.AllFolders];
    private WinoSearchScopeBar _scopeBar = null!;
    private string _searchText = string.Empty;

    private NSView BuildScopeBar()
    {
        // No translation key exists for "Done".
        _scopeBar = new WinoSearchScopeBar(Translator.SettingsAppPreferences_SearchMode_Local, Translator.SettingsAppPreferences_SearchMode_Online, "Done")
        {
            Hidden = true
        };
        _scopeBar.SetScopes([Translator.SearchBar_CurrentFolder, Translator.SearchBar_ScopeSubfolders, Translator.SearchBar_AllFolders], 0);
        _scopeBar.ReachChanged += (_, _) => Observe(RerunSearchAsync());
        _scopeBar.ScopeChanged += (_, index) =>
        {
            if (index < 0 || index >= Scopes.Length || Scopes[index] == ViewModel.SearchScope) return;
            ViewModel.SearchScope = Scopes[index];
            Observe(RerunSearchAsync());
        };
        _scopeBar.DoneClicked += (_, _) => Observe(SearchClearedAsync());
        _scopeBar.ChipClicked += (_, chip) => Observe(ToggleChipAsync(chip));
        return _scopeBar;
    }

    private void BindSearch()
    {
        Bind(nameof(ViewModel.IsInSearchMode), vm => vm.IsInSearchMode, _ => UpdateScopeBar());
        Bind(nameof(ViewModel.SearchFilters), vm => vm.SearchFilters, _ => UpdateScopeBar());
        Bind(nameof(ViewModel.SearchScope), vm => vm.SearchScope, scope => _scopeBar.SetScopes(
            [Translator.SearchBar_CurrentFolder, Translator.SearchBar_ScopeSubfolders, Translator.SearchBar_AllFolders], Array.IndexOf(Scopes, scope)));
        Bind(nameof(ViewModel.AreSearchResultsOnline), vm => vm.AreSearchResultsOnline, _ => UpdateScopeBar());
        Bind(nameof(ViewModel.IsEmpty), vm => vm.IsEmpty, _ => UpdateScopeBar());
    }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(_searchText) || ViewModel.IsInSearchMode || ViewModel.ActiveSearchFilterCount > 0;

    private void UpdateScopeBar()
    {
        _scopeBar.Hidden = !IsSearchActive;
        if (_scopeBar.Hidden) return;
        _scopeBar.Status = ViewModel.IsInSearchMode
            ? $"{(ViewModel.AreSearchResultsOnline ? Translator.SettingsAppPreferences_SearchMode_Online : Translator.SettingsAppPreferences_SearchMode_Local)} · {ViewModel.MailCollection.Count}"
            : string.Empty;

        var filters = ViewModel.SearchFilters ?? MailSearchFilters.Empty;
        var chips = new List<WinoSearchChip>();
        foreach (var chip in MailSearchFilterChip.From(filters))
        {
            if (chip.Kind is MailSearchFilterKind.Sender or MailSearchFilterKind.Subject or MailSearchFilterKind.Date)
                chips.Add(new WinoSearchChip(chip.Kind, chip.Text, null, true, true));
        }
        chips.Add(new WinoSearchChip(MailSearchFilterKind.ReadStatus, Translator.SearchBar_Unread, null, filters.ReadStatus == MailReadStatusFilter.Unread));
        chips.Add(new WinoSearchChip(MailSearchFilterKind.Attachments, Translator.SearchBar_HasAttachments, null, filters.HasAttachments));
        chips.Add(new WinoSearchChip(MailSearchFilterKind.Flagged, Translator.SearchBar_Flagged, null, filters.IsFlagged));
        _scopeBar.SetChips(chips);
    }

    private async Task ToggleChipAsync(WinoSearchChip chip)
    {
        if (chip.Tag is not MailSearchFilterKind kind) return;
        var filters = ViewModel.SearchFilters ?? MailSearchFilters.Empty;
        ViewModel.SearchFilters = kind switch
        {
            MailSearchFilterKind.ReadStatus => filters with { ReadStatus = filters.ReadStatus == MailReadStatusFilter.Unread ? MailReadStatusFilter.All : MailReadStatusFilter.Unread },
            MailSearchFilterKind.Attachments => filters with { HasAttachments = !filters.HasAttachments },
            MailSearchFilterKind.Flagged => filters with { IsFlagged = !filters.IsFlagged },
            _ => MailSearchFilterChip.Remove(filters, kind)
        };
        UpdateScopeBar();
        await RunSearchOrCloseAsync(_searchText);
    }

    public Task SearchTextChangedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        ViewModel.SearchQuery = _searchText;
        OnUI(UpdateScopeBar);
        if (!string.IsNullOrWhiteSpace(_searchText)) return Task.CompletedTask;

        // Clearing the text keeps a filtered search on screen; it now runs on the filters alone.
        ViewModel.IsOnlineSearchButtonVisible = false;
        return RunSearchOrCloseAsync(string.Empty);
    }

    public Task SearchSubmittedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        OnUI(UpdateScopeBar);
        return RunSearchOrCloseAsync(_searchText);
    }

    public async Task SearchClearedAsync()
    {
        _searchText = string.Empty;
        ViewModel.SearchFilters = MailSearchFilters.Empty;
        ViewModel.IsOnlineSearchButtonVisible = false;
        ViewModel.SetSearchCriteria(MailSearchCriteria.Empty, []);
        await ViewModel.PerformSearchAsync();
        OnUI(() => { _scopeBar.IsOnline = false; UpdateScopeBar(); });
    }

    private Task RerunSearchAsync() => RunSearchOrCloseAsync(_searchText);

    private async Task RunOnlineSearchAsync()
    {
        await Dispatcher.ExecuteOnUIThread(() => _scopeBar.IsOnline = true);
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
        ViewModel.SetSearchCriteria(MailSearchCriteria.Empty, []);
        await ViewModel.PerformSearchAsync();
        OnUI(UpdateScopeBar);
    }

    private async Task RunMailSearchAsync(string query)
    {
        bool online = false;
        await Dispatcher.ExecuteOnUIThread(() => online = _scopeBar.IsOnline);
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
}
