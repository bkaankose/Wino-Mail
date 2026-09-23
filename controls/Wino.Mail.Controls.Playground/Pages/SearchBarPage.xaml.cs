using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class SearchBarPage : Page, IDisposable
{
    private bool _disposed;
    private int _eventCount;
    private FlyoutBase? _headerFlyout;
    private FlyoutBase? _filterFlyout;
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

    private void SearchHeaderItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item) return;
        SearchBar.SelectedHeaderButtonTitle = item.Text;
        AddTrace($"Header selection: {item.Text}");
    }

    private void CompactLayoutToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null) return;
        ApplyCompactLayout(CompactLayoutToggle.IsOn);
        AddTrace($"Compact layout: {CompactLayoutToggle.IsOn}");
    }

    // Mirrors the shell title bar: a centered field with room around it to grow into on focus, and
    // only the icon, without the width floor, when compact.
    private void ApplyCompactLayout(bool isCompact)
    {
        SearchBar.IsCompact = isCompact;
        SearchBar.MinWidth = isCompact ? 0 : 280;
        SearchBar.MaxWidth = isCompact ? double.PositiveInfinity : 360;
        SearchBar.HorizontalAlignment = isCompact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    private void PageButtonsToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null) return;
        _headerFlyout ??= SearchBar.HeaderFlyout;
        _filterFlyout ??= SearchBar.FilterFlyout;
        SearchBar.HeaderFlyout = PageButtonsToggle.IsOn ? _headerFlyout : null;
        SearchBar.FilterFlyout = PageButtonsToggle.IsOn ? _filterFlyout : null;
        AddTrace($"Page buttons: {PageButtonsToggle.IsOn}");
    }

    private void LightThemeToggled(object sender, RoutedEventArgs e)
    {
        RequestedTheme = LightThemeToggle.IsOn ? ElementTheme.Light : ElementTheme.Dark;
        AddTrace($"Theme: {RequestedTheme}");
    }

    private void EnabledToggled(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null) return;
        SearchBar.IsEnabled = EnabledToggle.IsOn;
        AddTrace($"Enabled: {EnabledToggle.IsOn}");
    }

    private void SearchBarTextChanged(object? sender, SearchBarTextChangedEventArgs e)
    {
        QueryStateText.Text = $"Query: “{e.Text}” · {(e.IsUserInput ? "typed" : "set")}";
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
        AddTrace($"Submitted · {e.Mode} · {e.Origin} · {e.Reach}");
    }

    private void SearchBarDismissed(object? sender, EventArgs e) => AddTrace("Dismissed");

    private void SearchBarClearHistoryRequested(object? sender, EventArgs e)
    {
        _historyByMode[SearchBar.Mode].Clear();
        AddTrace($"Cleared {SearchBar.Mode} history");
    }

    private void ApplyMode(SearchBarMode mode)
    {
        if (SearchBar is null) return;

        SearchBar.Mode = mode;
        SearchBar.Text = string.Empty;
        SearchBar.SearchHistoryItemsSource = _historyByMode[mode];
        SearchBar.ItemsSource = Suggestions;
        ApplyCompactLayout(CompactLayoutToggle?.IsOn == true);
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
        LastEventText.Text = $"{++_eventCount}. {message}";
        EventTrace.Insert(0, $"{DateTime.Now:T}  {message}");
        while (EventTrace.Count > 12) EventTrace.RemoveAt(EventTrace.Count - 1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SearchBar.SearchSubmitted -= SearchBarSubmitted;
        SearchBar.SearchTextChanged -= SearchBarTextChanged;
        SearchBar.ClearSearchHistoryRequested -= SearchBarClearHistoryRequested;
        SearchBar.SearchDismissed -= SearchBarDismissed;
        ModeComboBox.SelectionChanged -= ModeComboBoxSelectionChanged;
        CompactLayoutToggle.Toggled -= CompactLayoutToggled;
        PageButtonsToggle.Toggled -= PageButtonsToggled;
        EnabledToggle.Toggled -= EnabledToggled;
        LightThemeToggle.Toggled -= LightThemeToggled;
        Bindings.StopTracking();
        SearchBar.Dispose();
        ResultsList.ItemsSource = null;
        ModeComboBox.ItemsSource = null;
        Content = null;
        GC.SuppressFinalize(this);
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
