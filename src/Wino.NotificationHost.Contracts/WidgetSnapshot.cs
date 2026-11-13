using System.Text.Json.Serialization;

namespace Wino.NotificationHost.Contracts;

public enum WidgetSnapshotState
{
    Ready,
    NoAccounts,
    Disabled
}

/// <summary>
/// What the Widgets Board shows, written by the main application and rendered by Wino.WidgetHost. Every text is
/// already localized and privacy-filtered, so the host needs neither translations nor the database.
/// </summary>
public sealed class WidgetSnapshot
{
    public WidgetSnapshotState State { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public WidgetStrings Strings { get; set; } = new();
    public WidgetMailSection Mail { get; set; } = new();
    public WidgetCalendarSection Calendar { get; set; } = new();
    public WidgetTasksSection Tasks { get; set; } = new();
    public List<WidgetPersonItem> People { get; set; } = [];
}

public sealed class WidgetStrings
{
    public string Unread { get; set; } = string.Empty;
    public string NewMail { get; set; } = string.Empty;
    public string OpenInbox { get; set; } = string.Empty;
    public string NoUnreadMail { get; set; } = string.Empty;
    public string NewEvent { get; set; } = string.Empty;
    public string Join { get; set; } = string.Empty;
    public string Today { get; set; } = string.Empty;
    public string Tomorrow { get; set; } = string.Empty;
    public string NoUpcomingEvents { get; set; } = string.Empty;
    public string StartsInFormat { get; set; } = string.Empty;
    public string Now { get; set; } = string.Empty;
    public string MyDay { get; set; } = string.Empty;
    public string AddTask { get; set; } = string.Empty;
    public string NoTasks { get; set; } = string.Empty;
    public string Favorites { get; set; } = string.Empty;
    public string NoFavorites { get; set; } = string.Empty;
    public string NoAccountsMail { get; set; } = string.Empty;
    public string NoAccountsCalendar { get; set; } = string.Empty;
    public string NoAccountsTasks { get; set; } = string.Empty;
    public string NoAccountsPeople { get; set; } = string.Empty;
    public string NoAccountsShort { get; set; } = string.Empty;
    public string OpenWino { get; set; } = string.Empty;
    public string Disabled { get; set; } = string.Empty;
    public string DisabledShort { get; set; } = string.Empty;
    public string EnableWidgets { get; set; } = string.Empty;
}

public sealed class WidgetMailSection
{
    public int UnreadTotal { get; set; }
    public List<WidgetMailItem> Items { get; set; } = [];
}

public sealed class WidgetMailItem
{
    public string Id { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public bool IsHidden { get; set; }
}

public sealed class WidgetCalendarSection
{
    public string DayName { get; set; } = string.Empty;
    public string DayNumber { get; set; } = string.Empty;
    public string DateText { get; set; } = string.Empty;
    public string TodayCountText { get; set; } = string.Empty;
    public List<WidgetEventItem> Events { get; set; } = [];
}

public sealed class WidgetEventItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string TimeText { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public bool IsTomorrow { get; set; }
    public bool IsAllDay { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
}

public sealed class WidgetTasksSection
{
    public int Completed { get; set; }
    public int Total { get; set; }
    public string ProgressText { get; set; } = string.Empty;
    public List<WidgetTaskItem> Items { get; set; } = [];
}

public sealed class WidgetTaskItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool IsImportant { get; set; }
}

public sealed class WidgetPersonItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>A small JPEG data URI, or empty when the contact has no picture.</summary>
    public string Picture { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WidgetSnapshot))]
public sealed partial class WidgetSnapshotJsonContext : JsonSerializerContext;
