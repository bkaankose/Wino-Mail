using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class SearchBarPage : Page
{
    private readonly Dictionary<SearchBarMode, ObservableCollection<string>> _historyByMode = new()
    {
        [SearchBarMode.Mail] =
        [
            "Quarterly roadmap from Alex",
            "Invoices with attachments",
            "Unread messages from GitHub",
            "Travel plans in July",
            "Flagged customer feedback",
            "Project Northstar",
            "Receipts from last month",
            "Security alerts",
        ],
        [SearchBarMode.Contacts] = ["Ada Lovelace", "Design team", "Contoso support", "People at Fabrikam"],
        [SearchBarMode.Calendar] = ["Project Northstar review", "Dentist appointment", "Team planning", "Flight to Warsaw"],
        [SearchBarMode.Tasks] = ["Finish release notes", "Prepare sprint demo", "Renew domain"],
        [SearchBarMode.Settings] = [],
    };

    private readonly SearchBarSuggestion[] _calendarSuggestions =
    [
        new("Project Northstar review", "Work • Product calendar • Tomorrow, 10:00"),
        new("Project Northstar retrospective", "Work • Team calendar • Friday, 15:30"),
        new("Northstar customer call", "Personal • Monday, 09:00"),
        new("Dentist appointment", "Personal • Thursday, 14:00"),
    ];

    private readonly SearchBarSuggestion[] _settingsSuggestions =
    [
        new("Mail appearance", "Personalization • Message list density and theme"),
        new("Mail notifications", "Notifications • Sound, banners, and badges"),
        new("Manage accounts", "Accounts • Add, remove, or merge accounts"),
        new("Signatures", "Compose • Signatures for each account"),
    ];

    private readonly SearchBarContactSuggestion[] _contacts =
    [
        new() { DisplayName = "Alex Morgan", Address = "alex@contoso.com", Initials = "AM" },
        new() { DisplayName = "Ada Lovelace", Address = "ada@fabrikam.com", Initials = "AL" },
        new() { DisplayName = "Wino Support", Address = "support@wino-mail.app", Initials = "WS" },
    ];

    public ObservableCollection<SearchModeOption> ModeOptions { get; } =
    [
        new(SearchBarMode.Mail, "Mail"),
        new(SearchBarMode.Contacts, "Contacts"),
        new(SearchBarMode.Calendar, "Calendar"),
        new(SearchBarMode.Tasks, "Tasks"),
        new(SearchBarMode.Settings, "Settings"),
    ];

    public ObservableCollection<SearchBarSuggestion> Suggestions { get; } = [];

    public ObservableCollection<string> Results { get; } = [];

    public ObservableCollection<string> EventTrace { get; } = [];

    public SearchBarPage()
    {
        InitializeComponent();
        ApplyMode(SearchBarMode.Mail);
    }

    private void ModeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeComboBox.SelectedItem is SearchModeOption option) ApplyMode(option.Mode);
    }

    private void SemanticAvailabilityToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null) return;
        SearchBar.IsSemanticSearchAvailable = SemanticAvailabilityToggle.IsOn;
        AddTrace($"Intelligence availability: {SemanticAvailabilityToggle.IsOn}");
    }

    private void SemanticBusyToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null) return;
        SearchBar.IsSemanticSearchBusy = SemanticBusyToggle.IsOn;
        AddTrace($"Semantic busy: {SemanticBusyToggle.IsOn}");
    }

    private void SemanticEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null) return;
        SearchBar.IsSemanticSearchEnabled = SemanticEnabledToggle.IsOn;
        AddTrace($"Meaning enabled: {SearchBar.IsSemanticSearchEnabled}");
    }

    private void CompactLayoutToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null) return;
        SearchBar.IsCompact = CompactLayoutToggle.IsOn;
        AddTrace($"Compact layout: {CompactLayoutToggle.IsOn}");
    }

    private void SearchBarTextChanged(object? sender, SearchBarTextChangedEventArgs e)
    {
        if (!e.IsUserInput) return;

        Suggestions.Clear();
        var source = SearchBar.Mode switch
        {
            SearchBarMode.Calendar => _calendarSuggestions,
            SearchBarMode.Settings => _settingsSuggestions,
            _ => [],
        };

        if (string.IsNullOrWhiteSpace(e.Text)) return;
        foreach (var suggestion in source.Where(item =>
                     item.Title.Contains(e.Text, StringComparison.OrdinalIgnoreCase) ||
                     item.Subtitle.Contains(e.Text, StringComparison.OrdinalIgnoreCase)))
        {
            Suggestions.Add(suggestion);
        }
    }

    private void SearchBarSubmitted(object? sender, SearchBarSubmittedEventArgs e)
    {
        if (e.Mode != SearchBarMode.Settings)
        {
            var history = _historyByMode[e.Mode];
            var existing = history.FirstOrDefault(item => string.Equals(item, e.QueryText, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) history.Remove(existing);
            history.Insert(0, e.QueryText);
            while (history.Count > 8) history.RemoveAt(history.Count - 1);
        }

        Results.Clear();
        if (e.Mode is SearchBarMode.Calendar or SearchBarMode.Settings)
        {
            Results.Add(e.ChosenSuggestion is SearchBarSuggestion suggestion
                ? $"Opened: {suggestion.Title}"
                : $"Best match: {e.QueryText}");
        }
        else
        {
            Results.Add($"Re: {e.QueryText} — Alex Morgan");
            Results.Add($"Notes mentioning “{e.QueryText}” — Product team");
            Results.Add($"Follow-up: {e.QueryText} — Wino Mail");
        }

        ResultSummary.Text = $"{Results.Count} simulated result(s) for “{e.QueryText}”.";
        AddTrace($"Submitted · {e.Mode} · {e.Origin} · semantic={e.IsSemanticSearchEnabled} · {e.Filters}");
    }

    private void SearchBarClearHistoryRequested(object? sender, EventArgs e)
    {
        _historyByMode[SearchBar.Mode].Clear();
        AddTrace($"Cleared {SearchBar.Mode} history");
    }

    private void SearchBarOptionsChanged(object? sender, SearchBarFilterSnapshot e)
        => AddTrace($"Options · {e}");

    private void SearchBarSenderSuggestionsRequested(object? sender, SearchBarSenderQueryEventArgs e)
    {
        SearchBar.SenderSuggestions = _contacts
            .Where(contact => contact.DisplayName.Contains(e.QueryText, StringComparison.OrdinalIgnoreCase) ||
                              contact.Address.Contains(e.QueryText, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        AddTrace($"Sender suggestions: {e.QueryText}");
    }

    private void ApplyMode(SearchBarMode mode)
    {
        if (SearchBar is null) return;

        SearchBar.Mode = mode;
        SearchBar.Text = string.Empty;
        SearchBar.SearchHistoryItemsSource = _historyByMode[mode];
        SearchBar.ItemsSource = Suggestions;
        SearchBar.IsSemanticSearchAvailable = SemanticAvailabilityToggle?.IsOn == true;
        SearchBar.IsSemanticSearchBusy = SemanticBusyToggle?.IsOn == true;
        SearchBar.IsSemanticSearchEnabled = SemanticEnabledToggle?.IsOn == true;
        SearchBar.IsCompact = CompactLayoutToggle?.IsOn == true;
        SearchBar.ScopeOptionsSource = new SearchBarOptionItem[]
        {
            new((int)SearchBarScope.CurrentFolder, "Current folder"),
            new((int)SearchBarScope.CurrentAccount, "Current account"),
            new((int)SearchBarScope.AllAccounts, "All accounts"),
        };
        SearchBar.ReachOptionsSource = new SearchBarOptionItem[]
        {
            new((int)SearchBarReach.DownloadedOnly, "Downloaded only"),
            new((int)SearchBarReach.IncludeServer, "Include mail server"),
        };
        SearchBar.DateOptionsSource = new SearchBarOptionItem[]
        {
            new((int)SearchBarDateRange.AnyTime, "Any time"),
            new((int)SearchBarDateRange.Today, "Today"),
            new((int)SearchBarDateRange.LastSevenDays, "Last 7 days"),
            new((int)SearchBarDateRange.LastThirtyDays, "Last 30 days"),
        };
        SearchBar.PlaceholderText = mode switch
        {
            SearchBarMode.Mail => "Search mail",
            SearchBarMode.Contacts => "Search contacts",
            SearchBarMode.Calendar => "Search events",
            SearchBarMode.Settings => "Search settings",
            _ => "Search",
        };

        Suggestions.Clear();
        Results.Clear();
        ResultSummary.Text = mode switch
        {
            SearchBarMode.Calendar => "Type to preview event suggestion templates.",
            SearchBarMode.Settings => "Type to preview settings navigation suggestions. History is disabled.",
            _ => "Submit a query to preview page-level results.",
        };
        AddTrace($"Mode changed: {mode}");
    }

    private void AddTrace(string message)
    {
        EventTrace.Insert(0, $"{DateTime.Now:T}  {message}");
        while (EventTrace.Count > 12) EventTrace.RemoveAt(EventTrace.Count - 1);
    }
}

public sealed class SearchModeOption(SearchBarMode mode, string title)
{
    public SearchModeOption() : this(SearchBarMode.Mail, string.Empty)
    {
    }

    public SearchBarMode Mode { get; set; } = mode;

    public string Title { get; set; } = title;
}
