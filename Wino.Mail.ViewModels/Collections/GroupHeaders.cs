using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wino.Mail.ViewModels.Collections;

/// <summary>
/// Base class for group headers in the flat collection
/// </summary>
public abstract partial class GroupHeaderBase : ObservableObject
{
    [ObservableProperty]
    private int itemCount;

    [ObservableProperty]
    private int unreadCount;

    protected GroupHeaderBase(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    /// <summary>
    /// The unique key for this group (used for sorting and identification)
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// The display name shown in the UI
    /// </summary>
    public string DisplayName { get; }
}

/// <summary>
/// Group header for date-based grouping
/// </summary>
public partial class DateGroupHeader : GroupHeaderBase
{
    public DateGroupHeader(DateTime date) : base(date.ToString("yyyy-MM-dd"), FormatDisplayName(date))
    {
        Date = date;
    }

    /// <summary>
    /// The date this group represents
    /// </summary>
    public DateTime Date { get; }

    private static string FormatDisplayName(DateTime date)
    {
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);

        return date.Date switch
        {
            var d when d == today => "Today",
            var d when d == yesterday => "Yesterday",
            var d when d >= today.AddDays(-7) => date.ToString("dddd"), // This week
            var d when d.Year == today.Year => date.ToString("MMMM dd"), // This year
            _ => date.ToString("MMMM dd, yyyy") // Other years
        };
    }
}

/// <summary>
/// Group header for sender name-based grouping
/// </summary>
public partial class SenderGroupHeader : GroupHeaderBase
{
    public SenderGroupHeader(string senderName) : base(senderName, senderName)
    {
        SenderName = senderName;
    }

    /// <summary>
    /// The sender name this group represents
    /// </summary>
    public string SenderName { get; }
}
