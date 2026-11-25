using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Search;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Searching the mail list (port of macOS <c>MailListPageViewController.Search</c>): the header entry is the query, the
/// scope bar above the list holds reach, scope and filter tokens, and the online panel under the list offers a server
/// search when the local one ran. A search always goes through <see cref="MailListPageViewModel.SearchAsync"/> with the
/// scope bar's reach.
/// </summary>
internal sealed partial class MailListView : IShellSearchTarget
{
    private readonly CancellationTokenSource _searchCancellation = new();
    private WinoSearchScopeBar? _scopeBar;
    private Gtk.Button? _onlinePanel;
    private string _searchText = string.Empty;

    /// <summary>The Filters token was clicked; the argument is the anchor. The token is shown while someone subscribes.</summary>
    internal event Action<Gtk.Widget>? SearchFiltersRequested;

    internal bool IsSearchReachOnline => _scopeBar?.IsOnline == true;

    private static string[] ScopeTitles => [Translator.SearchBar_CurrentFolder, Translator.SearchBar_ScopeSubfolders, Translator.SearchBar_AllFolders];

    private bool IsDefaultSearchOnline => ViewModel.PreferencesService.DefaultSearchMode == SearchMode.Online;

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(_searchText) || ViewModel.IsInSearchMode || ViewModel.ActiveSearchFilterCount > 0;

    partial void SetUpSearch()
    {
        var bar = new WinoSearchScopeBar(Translator.SettingsAppPreferences_SearchMode_Local, Translator.SettingsAppPreferences_SearchMode_Online, Translator.SearchBar_SearchIn, Translator.Buttons_Done);
        bar.Root.SetVisible(false);
        bar.SetScopes(ScopeTitles, MailListPageViewModel.SearchScopes.IndexOf(ViewModel.SearchScope));
        bar.IsOnline = IsDefaultSearchOnline;
        _scopeBar = bar;
        Bindings.Own(bar);

        var self = _self;
        bar.ReachChanged += () => self.Value?.Observe(self.Value.RerunSearchAsync());
        bar.ScopeChanged += index => self.Value?.ChangeScope(index);
        bar.DoneClicked += () => self.Value?.Observe(self.Value.DoneAsync());
        bar.ChipClicked += chip => self.Value?.Observe(self.Value.ToggleChipAsync(chip));
        bar.ClearAllClicked += () => self.Value?.ClearAllFilters();
        bar.FiltersClicked += anchor => self.Value?.SearchFiltersRequested?.Invoke(anchor);
        Bindings.Own(new ActionDisposable(() =>
        {
            _searchCancellation.Cancel();
            _searchCancellation.Dispose();
            if (self.Value is { } view) view.SearchFiltersRequested = null;
        }));

        var panel = Gtk.Button.New();
        panel.AddCssClass("flat");
        panel.AddCssClass(WinoCssClass.OnlineSearchPanel);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var glyph = WinoIcons.Label(WinoIconGlyph.Find, 18);
        glyph.AddCssClass("accent");
        glyph.SetValign(Gtk.Align.Center);
        var lines = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        var line1 = Gtk.Label.New(Translator.OnlineSearchTry_Line1);
        line1.AddCssClass("heading");
        line1.SetXalign(0);
        line1.SetWrap(true);
        var line2 = Gtk.Label.New(Translator.OnlineSearchTry_Line2);
        line2.AddCssClass("caption");
        line2.AddCssClass("dim-label");
        line2.SetXalign(0);
        line2.SetWrap(true);
        lines.Append(line1);
        lines.Append(line2);
        content.Append(glyph);
        content.Append(lines);
        panel.SetChild(content);
        panel.SetVisible(false);
        WinoA11y.Label(panel, $"{Translator.OnlineSearchTry_Line1} {Translator.OnlineSearchTry_Line2}");
        Bindings.Own(GtkSignals.OnClicked(panel, () => self.Value?.Observe(self.Value.RunOnlineSearchAsync())));
        _onlinePanel = panel;

        HeaderSlot.Append(bar.Root);
        FooterSlot.Append(panel);

        Bind.Bind(nameof(MailListPageViewModel.IsOnlineSearchButtonVisible), static vm => vm.IsOnlineSearchButtonVisible, panel.SetVisible);
        Bind.Bind(nameof(MailListPageViewModel.IsInSearchMode), static vm => vm.IsInSearchMode, _ => UpdateScopeBar());
        Bind.Bind(nameof(MailListPageViewModel.SearchFilters), static vm => vm.SearchFilters, _ => UpdateScopeBar());
        Bind.Bind(nameof(MailListPageViewModel.SearchScope), static vm => vm.SearchScope,
            scope => _scopeBar?.SetScopes(ScopeTitles, MailListPageViewModel.SearchScopes.IndexOf(scope)));
        Bind.Bind(nameof(MailListPageViewModel.AreSearchResultsOnline), static vm => vm.AreSearchResultsOnline, _ => UpdateScopeBar());
        Bind.Bind(nameof(MailListPageViewModel.IsEmpty), static vm => vm.IsEmpty, _ => UpdateScopeBar());
        Bind.Bind(nameof(MailListPageViewModel.SearchQuery), static vm => vm.SearchQuery, ShowSearchQuery);
        Applied += _ => UpdateScopeStatus();
    }

    public Task SearchTextChangedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        ViewModel.SearchQuery = _searchText;
        RunOnUI(UpdateScopeBar);
        if (!string.IsNullOrWhiteSpace(_searchText)) return Task.CompletedTask;

        // Clearing the text keeps a filtered search on screen; it now runs on the filters alone.
        ViewModel.IsOnlineSearchButtonVisible = false;
        return RunSearchOrCloseAsync(string.Empty);
    }

    public Task SearchSubmittedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        RunOnUI(UpdateScopeBar);
        return RunSearchOrCloseAsync(_searchText);
    }

    public async Task SearchClearedAsync()
    {
        _searchText = string.Empty;
        await ViewModel.ClearSearchAsync();
        RunOnUI(() =>
        {
            if (_scopeBar is { } bar) bar.IsOnline = IsDefaultSearchOnline;
            UpdateScopeBar();
        });
    }

    /// <summary>Windows <c>ApplySearchFilterEditorAsync</c>: the editor's keywords become the search text.</summary>
    internal Task ApplySearchFilterEditorAsync()
    {
        var editor = ViewModel.SearchFilterEditor;
        var keywords = (editor.Keywords ?? string.Empty).Trim();
        ViewModel.SearchScope = editor.Scope;
        ViewModel.SearchFilters = editor.ToFilters();
        _searchText = keywords;
        ViewModel.SearchQuery = keywords;
        _navigation.Shell?.SetSearchText(keywords);
        UpdateScopeBar();
        return RunSearchOrCloseAsync(keywords);
    }

    /// <summary>A query set outside the entry (the <c>MailSearchRequested</c> message, the filter editor) shows in the header entry.</summary>
    private void ShowSearchQuery(string? query)
    {
        var text = query ?? string.Empty;
        if (!string.Equals(text.Trim(), _searchText.Trim(), StringComparison.Ordinal))
        {
            _searchText = text;
            UpdateScopeBar();
        }
        _navigation.Shell?.SetSearchText(text);
    }

    private void ChangeScope(int index)
    {
        var scopes = MailListPageViewModel.SearchScopes;
        if (index < 0 || index >= scopes.Length || scopes[index] == ViewModel.SearchScope) return;
        ViewModel.SearchScope = scopes[index];
        Observe(RerunSearchAsync());
    }

    /// <summary>Windows <c>SearchFilterClearAllClicked</c>.</summary>
    private void ClearAllFilters()
    {
        ViewModel.SearchFilters = MailSearchFilters.Empty;
        UpdateScopeBar();
        Observe(RunSearchOrCloseAsync(_searchText));
    }

    private async Task DoneAsync()
    {
        await SearchClearedAsync();
        RunOnUI(() => _navigation.Shell?.SetSearchText(string.Empty));
    }

    private void UpdateScopeBar()
    {
        if (IsReleased || _scopeBar is not { } bar) return;
        var active = IsSearchActive;
        bar.Root.SetVisible(active);
        if (!active) return;
        UpdateScopeStatus();

        var filters = ViewModel.SearchFilters ?? MailSearchFilters.Empty;
        var chips = new List<WinoSearchChip>();
        // Value filters are removable tokens; "Read" is one too, since the Unread toggle below cannot show it.
        foreach (var chip in MailSearchFilterChip.From(filters))
        {
            if (chip.Kind is MailSearchFilterKind.Sender or MailSearchFilterKind.Subject or MailSearchFilterKind.Date
                || (chip.Kind == MailSearchFilterKind.ReadStatus && filters.ReadStatus == MailReadStatusFilter.Read))
                chips.Add(new WinoSearchChip((int)chip.Kind, chip.Text, IsActive: true, IsRemovable: true, RemoveLabel: chip.RemoveText));
        }
        chips.Add(new WinoSearchChip((int)MailSearchFilterKind.ReadStatus, Translator.SearchBar_Unread, filters.ReadStatus == MailReadStatusFilter.Unread));
        chips.Add(new WinoSearchChip((int)MailSearchFilterKind.Attachments, Translator.SearchBar_HasAttachments, filters.HasAttachments));
        chips.Add(new WinoSearchChip((int)MailSearchFilterKind.Flagged, Translator.SearchBar_Flagged, filters.IsFlagged));
        bar.SetChips([.. chips]);
        bar.SetFilters(SearchFiltersRequested is null ? null : Translator.SearchBar_Filters, ViewModel.ActiveSearchFilterCount);
        bar.SetClearAll(Translator.SearchBar_ClearAllFilters, ViewModel.ActiveSearchFilterCount);
    }

    private void UpdateScopeStatus()
    {
        if (IsReleased || _scopeBar is not { } bar || !bar.Root.GetVisible()) return;
        bar.SetStatus(ViewModel.IsInSearchMode
            ? $"{(ViewModel.AreSearchResultsOnline ? Translator.SettingsAppPreferences_SearchMode_Online : Translator.SettingsAppPreferences_SearchMode_Local)} · {ViewModel.MailCollection.Count}"
            : string.Empty);
    }

    private async Task ToggleChipAsync(WinoSearchChip chip)
    {
        var kind = (MailSearchFilterKind)chip.Tag;
        var filters = ViewModel.SearchFilters ?? MailSearchFilters.Empty;
        ViewModel.SearchFilters = kind switch
        {
            // The Unread toggle; a removable "Read" token clears the status instead.
            MailSearchFilterKind.ReadStatus when chip.IsRemovable => filters with { ReadStatus = MailReadStatusFilter.All },
            MailSearchFilterKind.ReadStatus => filters with { ReadStatus = filters.ReadStatus == MailReadStatusFilter.Unread ? MailReadStatusFilter.All : MailReadStatusFilter.Unread },
            MailSearchFilterKind.Attachments => filters with { HasAttachments = !filters.HasAttachments },
            MailSearchFilterKind.Flagged => filters with { IsFlagged = !filters.IsFlagged },
            _ => MailSearchFilterChip.Remove(filters, kind)
        };
        UpdateScopeBar();
        await RunSearchOrCloseAsync(_searchText);
    }

    private Task RerunSearchAsync() => RunSearchOrCloseAsync(_searchText);

    private async Task RunOnlineSearchAsync()
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_scopeBar is { } bar) bar.IsOnline = true;
        });
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
        RunOnUI(UpdateScopeBar);
    }

    private async Task RunMailSearchAsync(string query)
    {
        var online = false;
        await Dispatcher.ExecuteOnUIThread(() => online = _scopeBar?.IsOnline == true);
        if (IsReleased) return;
        await ViewModel.SearchAsync(query, online, _searchCancellation.Token);
        RunOnUI(UpdateScopeBar);
    }

#if DEBUG
    internal WinoSearchScopeBar? DebugScopeBar => _scopeBar;

    internal string DebugSearchText => _searchText;

    internal bool DebugOnlinePanelVisible => _onlinePanel?.GetVisible() == true;

    internal void DebugChangeScope(int index) => ChangeScope(index);

    internal void DebugClearAll() => ClearAllFilters();

    internal Task DebugToggleChipAsync(WinoSearchChip chip) => ToggleChipAsync(chip);

    internal Task DebugRunOnlineSearchAsync() => RunOnlineSearchAsync();

    internal Task DebugRerunSearchAsync() => RerunSearchAsync();

    internal Task DebugDoneAsync() => DoneAsync();
#endif
}
