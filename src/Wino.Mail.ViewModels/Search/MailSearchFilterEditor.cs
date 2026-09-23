using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Search;

public sealed class MailSearchFilterOption(int value, string title)
{
    public int Value { get; } = value;

    public string Title { get; } = title;

    public override string ToString() => Title;
}

public enum MailSearchFilterKind
{
    Sender,
    Subject,
    Date,
    ReadStatus,
    Attachments,
    Flagged,
}

/// <summary>One active filter shown above the mail list. Removing it searches again without it.</summary>
public sealed class MailSearchFilterChip(MailSearchFilterKind kind, string text)
{
    public MailSearchFilterKind Kind { get; } = kind;

    public string Text { get; } = text;

    public string RemoveText { get; } = string.Format(Translator.SearchBar_RemoveFilter, text);

    public static IReadOnlyList<MailSearchFilterChip> From(MailSearchFilters filters)
    {
        var chips = new List<MailSearchFilterChip>();
        if (filters is null)
            return chips;

        if (!string.IsNullOrWhiteSpace(filters.Sender))
            chips.Add(new(MailSearchFilterKind.Sender, $"{Translator.SearchBar_From}: {filters.Sender.Trim()}"));
        if (!string.IsNullOrWhiteSpace(filters.Subject))
            chips.Add(new(MailSearchFilterKind.Subject, $"{Translator.SearchBar_Subject}: {filters.Subject.Trim()}"));
        if (filters.HasDateFilter)
            chips.Add(new(MailSearchFilterKind.Date, $"{Translator.SearchBar_Date}: {GetDateText(filters)}"));
        if (filters.ReadStatus != MailReadStatusFilter.All)
            chips.Add(new(MailSearchFilterKind.ReadStatus, filters.ReadStatus == MailReadStatusFilter.Unread ? Translator.SearchBar_Unread : Translator.SearchBar_ReadStatusRead));
        if (filters.HasAttachments)
            chips.Add(new(MailSearchFilterKind.Attachments, Translator.SearchBar_HasAttachments));
        if (filters.IsFlagged)
            chips.Add(new(MailSearchFilterKind.Flagged, Translator.SearchBar_Flagged));

        return chips;
    }

    public static MailSearchFilters Remove(MailSearchFilters filters, MailSearchFilterKind kind) => kind switch
    {
        MailSearchFilterKind.Sender => filters with { Sender = string.Empty },
        MailSearchFilterKind.Subject => filters with { Subject = string.Empty },
        MailSearchFilterKind.Date => filters with { DateRange = MailSearchDateRange.AnyTime, CustomStartDate = null, CustomEndDate = null },
        MailSearchFilterKind.ReadStatus => filters with { ReadStatus = MailReadStatusFilter.All },
        MailSearchFilterKind.Attachments => filters with { HasAttachments = false },
        MailSearchFilterKind.Flagged => filters with { IsFlagged = false },
        _ => filters,
    };

    private static string GetDateText(MailSearchFilters filters) => filters.DateRange switch
    {
        MailSearchDateRange.Today => Translator.SearchBar_DateToday,
        MailSearchDateRange.LastSevenDays => Translator.SearchBar_DateLastSevenDays,
        MailSearchDateRange.LastThirtyDays => Translator.SearchBar_DateLastThirtyDays,
        _ => $"{filters.CustomStartDate?.ToShortDateString() ?? "…"} – {filters.CustomEndDate?.ToShortDateString() ?? "…"}",
    };
}

/// <summary>
/// The editable copy of the search filters behind the filter flyout. Edits apply only when the
/// user searches; closing the flyout another way leaves the search on screen unchanged.
/// </summary>
public sealed partial class MailSearchFilterEditor : ObservableObject
{
    public IReadOnlyList<MailSearchFilterOption> ScopeOptions { get; } = (MailSearchFilterOption[])
    [
        new((int)MailSearchScope.CurrentFolder, Translator.SearchBar_CurrentFolder),
        new((int)MailSearchScope.Subfolders, Translator.SearchBar_ScopeSubfolders),
        new((int)MailSearchScope.AllFolders, Translator.SearchBar_AllFolders),
    ];

    public IReadOnlyList<MailSearchFilterOption> DateRangeOptions { get; } = (MailSearchFilterOption[])
    [
        new((int)MailSearchDateRange.AnyTime, Translator.SearchBar_DateAnyTime),
        new((int)MailSearchDateRange.Today, Translator.SearchBar_DateToday),
        new((int)MailSearchDateRange.LastSevenDays, Translator.SearchBar_DateLastSevenDays),
        new((int)MailSearchDateRange.LastThirtyDays, Translator.SearchBar_DateLastThirtyDays),
        new((int)MailSearchDateRange.Custom, Translator.SearchBar_DateCustom),
    ];

    public IReadOnlyList<MailSearchFilterOption> ReadStatusOptions { get; } = (MailSearchFilterOption[])
    [
        new((int)MailReadStatusFilter.All, Translator.SearchBar_ReadStatusAll),
        new((int)MailReadStatusFilter.Unread, Translator.SearchBar_Unread),
        new((int)MailReadStatusFilter.Read, Translator.SearchBar_ReadStatusRead),
    ];

    public MailSearchFilterEditor()
    {
        SelectedScope = ScopeOptions[0];
        SelectedDateRange = DateRangeOptions[0];
        SelectedReadStatus = ReadStatusOptions[0];
    }

    [ObservableProperty]
    public partial MailSearchFilterOption SelectedScope { get; set; }

    [ObservableProperty]
    public partial string Sender { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Keywords { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomDateRange))]
    public partial MailSearchFilterOption SelectedDateRange { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? CustomStartDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? CustomEndDate { get; set; }

    [ObservableProperty]
    public partial MailSearchFilterOption SelectedReadStatus { get; set; }

    [ObservableProperty]
    public partial bool HasAttachments { get; set; }

    [ObservableProperty]
    public partial bool IsFlagged { get; set; }

    /// <summary>Local search reads stored fields only, so the keywords field says what it matches.</summary>
    [ObservableProperty]
    public partial bool IsLocalSearch { get; set; } = true;

    public bool IsCustomDateRange => SelectedDateRange?.Value == (int)MailSearchDateRange.Custom;

    public MailSearchScope Scope => (MailSearchScope)(SelectedScope?.Value ?? 0);

    partial void OnSelectedDateRangeChanged(MailSearchFilterOption value)
    {
        if (value?.Value != (int)MailSearchDateRange.Custom || CustomStartDate is not null || CustomEndDate is not null)
            return;

        var today = DateTimeOffset.Now.Date;
        CustomStartDate = today.AddDays(-29);
        CustomEndDate = today;
    }

    public void Load(MailSearchScope scope, string keywords, MailSearchFilters filters, bool isLocalSearch)
    {
        filters ??= MailSearchFilters.Empty;

        SelectedScope = Find(ScopeOptions, (int)scope);
        Keywords = keywords ?? string.Empty;
        Sender = filters.Sender;
        Subject = filters.Subject;
        CustomStartDate = filters.CustomStartDate is { } start ? new DateTimeOffset(start.Date) : null;
        CustomEndDate = filters.CustomEndDate is { } end ? new DateTimeOffset(end.Date) : null;
        SelectedDateRange = Find(DateRangeOptions, (int)filters.DateRange);
        SelectedReadStatus = Find(ReadStatusOptions, (int)filters.ReadStatus);
        HasAttachments = filters.HasAttachments;
        IsFlagged = filters.IsFlagged;
        IsLocalSearch = isLocalSearch;
    }

    public MailSearchFilters ToFilters()
    {
        var dateRange = (MailSearchDateRange)(SelectedDateRange?.Value ?? 0);
        var isCustom = dateRange == MailSearchDateRange.Custom;

        return new MailSearchFilters(
            (Sender ?? string.Empty).Trim(),
            (Subject ?? string.Empty).Trim(),
            dateRange,
            isCustom ? CustomStartDate?.Date : null,
            isCustom ? CustomEndDate?.Date : null,
            (MailReadStatusFilter)(SelectedReadStatus?.Value ?? 0),
            HasAttachments,
            IsFlagged);
    }

    /// <summary>Clears the filters. The scope and the keywords stay, as they belong to the search box.</summary>
    [RelayCommand]
    private void Reset()
    {
        Sender = string.Empty;
        Subject = string.Empty;
        CustomStartDate = null;
        CustomEndDate = null;
        SelectedDateRange = DateRangeOptions[0];
        SelectedReadStatus = ReadStatusOptions[0];
        HasAttachments = false;
        IsFlagged = false;
    }

    private static MailSearchFilterOption Find(IReadOnlyList<MailSearchFilterOption> options, int value)
        => options.FirstOrDefault(option => option.Value == value) ?? options[0];
}
