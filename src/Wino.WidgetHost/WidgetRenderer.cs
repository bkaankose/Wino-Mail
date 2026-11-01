using System.Text;
using System.Text.Json;
using Microsoft.Windows.Widgets;
using Wino.NotificationHost.Contracts;

namespace Wino.WidgetHost;

internal readonly record struct WidgetCard(string TemplateName, string Data);

/// <summary>
/// Turns the main application's snapshot into one widget's template and data. The host runs with invariant
/// globalization, so every date and time arrives already formatted; only the minutes until the next event are
/// computed here.
/// </summary>
internal static class WidgetRenderer
{
    public const string MailDefinition = "Wino_Mail";
    public const string CalendarDefinition = "Wino_Calendar";
    public const string TasksDefinition = "Wino_ToDo";
    public const string PeopleDefinition = "Wino_People";

    private const string IconColor = "#8A8A8A";
    private const string AccentColor = "#0F6CBD";

    public static WidgetCard Render(string definitionId, WidgetSize size, WidgetSnapshot? snapshot, DateTimeOffset now)
    {
        // Before Wino has run once there is no snapshot, and with it no translated text.
        if (snapshot is null)
            return Message(size, "Open Wino to set up this widget.", "Open Wino to start.", "Open Wino", WidgetActions.OpenWino, WidgetIcons.Open);

        var strings = snapshot.Strings;

        if (snapshot.State == WidgetSnapshotState.Disabled)
        {
            return Message(size, strings.Disabled, strings.DisabledShort, strings.EnableWidgets, WidgetActions.EnableWidgets, WidgetIcons.Settings);
        }

        if (snapshot.State == WidgetSnapshotState.NoAccounts)
        {
            var message = definitionId switch
            {
                CalendarDefinition => strings.NoAccountsCalendar,
                TasksDefinition => strings.NoAccountsTasks,
                PeopleDefinition => strings.NoAccountsPeople,
                _ => strings.NoAccountsMail
            };
            return Message(size, message, strings.NoAccountsShort, strings.OpenWino, WidgetActions.OpenWino, WidgetIcons.PersonAdd);
        }

        return definitionId switch
        {
            CalendarDefinition => Calendar(size, snapshot, now),
            TasksDefinition => Tasks(size, snapshot),
            PeopleDefinition => People(size, snapshot),
            _ => Mail(size, snapshot)
        };
    }

    public static bool IsKnownDefinition(string definitionId)
        => definitionId is MailDefinition or CalendarDefinition or TasksDefinition or PeopleDefinition;

    private static WidgetCard Message(WidgetSize size, string text, string shortText, string button, string verb, string icon)
        => new("message", Json(writer =>
        {
            writer.WriteString("text", size == WidgetSize.Small ? shortText : text);
            writer.WriteBoolean("isSmall", size == WidgetSize.Small);
            writer.WriteString("button", button);
            writer.WriteString("verb", verb);
            writer.WriteString("icon", Icon(icon, IconColor));
        }));

    private static WidgetCard Mail(WidgetSize size, WidgetSnapshot snapshot)
    {
        var mail = snapshot.Mail;
        var strings = snapshot.Strings;
        var take = size switch { WidgetSize.Small => 1, WidgetSize.Medium => 3, _ => 5 };

        return new WidgetCard(TemplateName("mail", size), Json(writer =>
        {
            writer.WriteBoolean("hasUnread", mail.UnreadTotal > 0);
            writer.WriteString("unreadCount", mail.UnreadTotal > 99 ? "99+" : mail.UnreadTotal.ToString());
            writer.WriteString("unreadLabel", strings.Unread);
            writer.WriteBoolean("hasItems", mail.Items.Count > 0);
            writer.WriteString("noItems", strings.NoUnreadMail);
            writer.WriteString("newest", mail.Items.FirstOrDefault()?.Sender ?? strings.NoUnreadMail);
            writer.WriteString("newMail", strings.NewMail);
            writer.WriteString("openInbox", strings.OpenInbox);
            writer.WriteString("newMailIcon", Icon(WidgetIcons.NewMail, IconColor));
            writer.WriteString("inboxIcon", Icon(WidgetIcons.Inbox, IconColor));
            writer.WriteStartArray("items");
            foreach (var item in mail.Items.Take(take))
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("sender", item.Sender);
                writer.WriteString("subject", item.Subject);
                writer.WriteString("preview", item.Preview);
                writer.WriteString("time", item.Time);
                writer.WriteBoolean("hasSubject", !string.IsNullOrWhiteSpace(item.Subject));
                writer.WriteBoolean("hasPreview", !string.IsNullOrWhiteSpace(item.Preview));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }));
    }

    private static WidgetCard Calendar(WidgetSize size, WidgetSnapshot snapshot, DateTimeOffset now)
    {
        var calendar = snapshot.Calendar;
        var strings = snapshot.Strings;
        var events = calendar.Events;
        var next = events.FirstOrDefault();

        return new WidgetCard(TemplateName("calendar", size), Json(writer =>
        {
            writer.WriteString("dayName", calendar.DayName);
            writer.WriteString("dayNumber", calendar.DayNumber);
            writer.WriteString("dateText", calendar.DateText);
            writer.WriteString("todayCount", calendar.TodayCountText);
            writer.WriteBoolean("hasEvents", next != null);
            writer.WriteString("noEvents", strings.NoUpcomingEvents);
            writer.WriteString("nextId", next?.Id ?? string.Empty);
            writer.WriteString("nextTitle", next?.Title ?? string.Empty);
            writer.WriteString("nextWhen", next == null ? string.Empty : When(next, strings, now));
            writer.WriteString("todayLabel", strings.Today);
            writer.WriteString("tomorrowLabel", strings.Tomorrow);
            writer.WriteString("newEvent", strings.NewEvent);
            writer.WriteString("join", strings.Join);
            writer.WriteString("newEventIcon", Icon(WidgetIcons.Add, IconColor));
            writer.WriteString("joinIcon", Icon(WidgetIcons.EventJoinOnline, AccentColor));

            var today = events.Where(static item => !item.IsTomorrow).Take(size == WidgetSize.Large ? 4 : 3).ToList();
            var tomorrow = size == WidgetSize.Large
                ? events.Where(static item => item.IsTomorrow).Take(Math.Max(2, 6 - today.Count)).ToList()
                : [];
            writer.WriteBoolean("hasToday", today.Count > 0);
            writer.WriteBoolean("hasTomorrow", tomorrow.Count > 0);
            WriteEvents(writer, "today", today, size);
            WriteEvents(writer, "tomorrow", tomorrow, size);
        }));
    }

    private static void WriteEvents(Utf8JsonWriter writer, string name, List<WidgetEventItem> events, WidgetSize size)
    {
        writer.WriteStartArray(name);
        foreach (var item in events)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("title", item.Title);
            writer.WriteString("time", size == WidgetSize.Large && !string.IsNullOrWhiteSpace(item.Location)
                ? $"{item.TimeText} · {item.Location}"
                : item.TimeText);
            writer.WriteBoolean("isOnline", item.IsOnline);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string When(WidgetEventItem item, WidgetStrings strings, DateTimeOffset now)
    {
        if (item.IsTomorrow)
            return $"{strings.Tomorrow} {item.TimeText}";

        if (item.Start <= now)
            return $"{item.TimeText} · {strings.Now}";

        var minutes = (int)Math.Ceiling((item.Start - now).TotalMinutes);
        return minutes <= 60
            ? $"{item.TimeText} · {string.Format(strings.StartsInFormat, minutes)}"
            : item.TimeText;
    }

    private static WidgetCard Tasks(WidgetSize size, WidgetSnapshot snapshot)
    {
        var tasks = snapshot.Tasks;
        var strings = snapshot.Strings;
        var take = size == WidgetSize.Large ? 5 : 3;

        return new WidgetCard(TemplateName("tasks", size), Json(writer =>
        {
            writer.WriteString("title", strings.MyDay);
            writer.WriteString("progress", tasks.ProgressText);
            writer.WriteNumber("percent", tasks.Total == 0 ? 0 : (int)Math.Round(100.0 * tasks.Completed / tasks.Total));
            writer.WriteBoolean("hasItems", tasks.Items.Count > 0);
            writer.WriteString("noItems", strings.NoTasks);
            writer.WriteString("next", tasks.Items.FirstOrDefault()?.Title ?? strings.NoTasks);
            writer.WriteString("addTask", strings.AddTask);
            writer.WriteString("addIcon", Icon(WidgetIcons.Add, IconColor));
            writer.WriteStartArray("items");
            foreach (var item in tasks.Items.Take(take))
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("title", item.Title);
                writer.WriteString("detail", item.Detail);
                writer.WriteBoolean("hasDetail", !string.IsNullOrWhiteSpace(item.Detail));
                writer.WriteBoolean("isImportant", item.IsImportant);
                writer.WriteString("circleIcon", Icon(WidgetIcons.Circle, IconColor));
                writer.WriteString("starIcon", Icon(WidgetIcons.Star, "#C27C0E"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }));
    }

    private static WidgetCard People(WidgetSize size, WidgetSnapshot snapshot)
    {
        var people = snapshot.People;
        var strings = snapshot.Strings;
        var take = size switch { WidgetSize.Small => 4, WidgetSize.Medium => 6, _ => 8 };
        var shown = people.Take(take).ToList();

        return new WidgetCard(TemplateName("people", size), Json(writer =>
        {
            writer.WriteString("title", strings.Favorites);
            writer.WriteBoolean("hasItems", shown.Count > 0);
            writer.WriteString("noItems", strings.NoFavorites);
            // The medium grid binds rows of three, the other sizes a flat list; only that one is sent, as pictures are large.
            if (size == WidgetSize.Medium)
            {
                writer.WriteStartArray("rows");
                foreach (var row in shown.Chunk(3))
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("people");
                    foreach (var person in row)
                        WritePerson(writer, person);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteStartArray("items");
                foreach (var person in shown)
                    WritePerson(writer, person);
                writer.WriteEndArray();
            }
        }));
    }

    private static void WritePerson(Utf8JsonWriter writer, WidgetPersonItem person)
    {
        writer.WriteStartObject();
        writer.WriteString("id", person.Id);
        writer.WriteString("name", person.Name);
        writer.WriteString("firstName", person.FirstName);
        writer.WriteString("avatar", string.IsNullOrEmpty(person.Picture) ? AvatarImage : person.Picture);
        writer.WriteString("email", person.Email);
        writer.WriteBoolean("hasEmail", !string.IsNullOrWhiteSpace(person.Email));
        writer.WriteString("mailIcon", Icon(WidgetIcons.Mail, IconColor));
        writer.WriteEndObject();
    }

    private static string TemplateName(string kind, WidgetSize size)
        => $"{kind}-{size switch { WidgetSize.Small => "small", WidgetSize.Medium => "medium", _ => "large" }}";

    private static string Icon(string paths, string color)
        => SvgDataUri($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1024 1024\" fill=\"{color}\">{paths}</svg>");

    // The Board's SVG renderer draws paths but no text, so the avatar is the person glyph rather than initials.
    private static readonly string AvatarImage = SvgDataUri(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1024 1024\">" +
        $"<circle cx=\"512\" cy=\"512\" r=\"512\" fill=\"{AccentColor}\"/>" +
        $"<g transform=\"translate(256 256) scale(0.5)\" fill=\"#FFFFFF\">{WidgetIcons.Person}</g></svg>");

    private static string SvgDataUri(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
