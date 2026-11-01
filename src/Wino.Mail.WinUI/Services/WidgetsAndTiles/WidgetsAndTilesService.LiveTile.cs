using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Wino.Core.Domain;
using Wino.Mail.WinUI.Activation;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.WinUI.Services.WidgetsAndTiles;

/// <summary>Which modes rotate on the Wino Mail tile.</summary>
public sealed record LiveTileModes(bool Mail, bool Calendar, bool Tasks);

/// <summary>The rotating Wino Mail Start tile, rendered from the same snapshot as the widgets.</summary>
public sealed partial class WidgetsAndTilesService
{
    private const string MailTag = "mail";
    private const string CalendarTag = "calendar";
    private const string TasksTag = "tasks";
    private const string ClearedFingerprint = "cleared";

    private string? _tileFingerprint;

    /// <summary>Returns a one-line description of what changed, or null when the tile already shows this content.</summary>
    private string? UpdateLiveTile(WidgetSnapshot? snapshot, LiveTileModes modes, bool showDetails)
    {
        var ready = snapshot is { State: WidgetSnapshotState.Ready };
        var now = DateTime.Now;
        var frames = ready ? CreateTileFrames(snapshot!, modes, showDetails, now) : [];
        var scheduled = ready && modes.Calendar ? CreateTomorrowCalendarFrame(snapshot!, now) : null;
        var fingerprint = frames.Count == 0 && scheduled == null
            ? ClearedFingerprint
            : string.Join("\n", frames.Select(static frame => frame.Xml)) + "\n" + scheduled?.Xml;

        if (fingerprint == _tileFingerprint)
            return null;

        var updater = TileUpdateManager.CreateTileUpdaterForApplication(AppEntryConstants.MailApplicationId);
        updater.Clear();
        foreach (var pending in updater.GetScheduledTileNotifications())
            updater.RemoveFromSchedule(pending);

        if (fingerprint == ClearedFingerprint)
        {
            _tileFingerprint = fingerprint;
            return "Live Tiles: cleared";
        }

        updater.EnableNotificationQueue(true);
        foreach (var frame in frames)
        {
            updater.Update(new TileNotification(LoadTileXml(frame.Xml))
            {
                Tag = frame.Tag,
                ExpirationTime = frame.Expires
            });
        }

        if (scheduled != null)
        {
            updater.AddToSchedule(new ScheduledTileNotification(LoadTileXml(scheduled.Xml), scheduled.DeliveryTime)
            {
                Tag = CalendarTag,
                Id = CalendarTag,
                ExpirationTime = scheduled.DeliveryTime.AddDays(1)
            });
        }

        _tileFingerprint = fingerprint;
        return $"Live Tiles: {frames.Count} frames ({string.Join(", ", frames.Select(static frame => frame.Tag))})";
    }

    private static List<TileFrame> CreateTileFrames(WidgetSnapshot snapshot, LiveTileModes modes, bool showDetails, DateTime now)
    {
        var frames = new List<TileFrame>();
        var midnight = now.Date.AddDays(1);
        var tomorrow = DateTimeOffset.Now.AddDays(1);

        if (modes.Mail && snapshot.Mail.UnreadTotal > 0)
            frames.Add(new TileFrame(MailTag, CreateMailTile(snapshot.Mail, showDetails), tomorrow));

        if (modes.Calendar)
        {
            var events = snapshot.Calendar.Events;
            var today = events.Where(item => !item.IsTomorrow).ToList();
            frames.Add(new TileFrame(CalendarTag, CreateCalendarTile(now, today, events), new DateTimeOffset(midnight)));
        }

        if (modes.Tasks && snapshot.Tasks.Total > 0)
            frames.Add(new TileFrame(TasksTag, CreateTasksTile(snapshot.Tasks, showDetails), tomorrow));

        return frames;
    }

    private static ScheduledTileFrame CreateTomorrowCalendarFrame(WidgetSnapshot snapshot, DateTime now)
    {
        var tomorrow = now.Date.AddDays(1);
        var events = snapshot.Calendar.Events.Where(static item => item.IsTomorrow).ToList();
        return new ScheduledTileFrame(CreateCalendarTile(tomorrow, events, events), new DateTimeOffset(tomorrow));
    }

    private static string CreateMailTile(WidgetMailSection mail, bool showDetails)
    {
        var unread = string.Format(Translator.LiveTiles_UnreadFormat, mail.UnreadTotal);
        var items = mail.Items;

        if (!showDetails || items.Count == 0)
        {
            var countOnly = TileText(unread, "subtitle");
            return Tile(countOnly, countOnly, countOnly);
        }

        var medium = TileText(items[0].Sender, "base") + TileText(items[0].Subject, "captionSubtle") + TileText(items[0].Preview, "captionSubtle");
        var wide = string.Concat(items.Take(2).Select(static item => TileText(item.Sender, "base") + TileText(item.Subject, "captionSubtle")));
        var large = TileText(unread, "captionSubtle") + string.Concat(items.Take(3).Select(static item =>
            TileText(item.Sender, "base") + TileText(item.Subject, "caption") + TileText(item.Preview, "captionSubtle")));

        return Tile(medium, wide, large);
    }

    private static string CreateCalendarTile(DateTime day, List<WidgetEventItem> dayEvents, List<WidgetEventItem> upcoming)
    {
        var dayNumber = TileText(day.Day.ToString(CultureInfo.CurrentCulture), "title");
        var dayName = TileText(day.ToString("dddd", CultureInfo.CurrentCulture), "captionSubtle");
        var count = string.Format(Translator.LiveTiles_EventsTodayFormat, dayEvents.Count);

        if (upcoming.Count == 0)
        {
            var empty = dayNumber + dayName + TileText(Translator.LiveTiles_NoMoreEventsToday, "caption");
            return Tile(empty, empty, empty);
        }

        var next = upcoming[0];
        var medium = dayNumber + dayName + TileText(next.Title, "base") + TileText($"{FormatStart(next, day)} · {count}", "captionSubtle");
        var wide =
            "<group><subgroup hint-weight=\"30\">" + dayNumber + dayName + "</subgroup><subgroup>" +
            string.Concat(upcoming.Take(2).Select(item => TileText(item.Title, "base") + TileText(FormatRange(item, day), "captionSubtle"))) +
            "</subgroup></group>";
        var large = dayNumber + TileText(day.ToString("D", CultureInfo.CurrentCulture), "captionSubtle") +
            string.Concat(upcoming.Take(4).Select(item => TileText(item.Title, "base") + TileText(FormatRange(item, day), "captionSubtle")));

        return Tile(medium, wide, large);
    }

    private static string CreateTasksTile(WidgetTasksSection tasks, bool showDetails)
    {
        var header = TileText(Translator.ToDoPage_MyDay, "base") + TileText(tasks.ProgressText, "captionSubtle");

        if (!showDetails || tasks.Items.Count == 0)
        {
            var left = header + TileText(string.Format(Translator.LiveTiles_TasksLeftFormat, tasks.Total - tasks.Completed), "caption");
            return Tile(left, left, left);
        }

        var medium = header + TileText(tasks.Items[0].Title, "caption");
        var wide = header + string.Concat(tasks.Items.Take(3).Select(static task => TileText(task.Title, "captionSubtle")));
        var large = header + string.Concat(tasks.Items.Take(5).Select(static task => TileText(task.Title, "caption")));
        return Tile(medium, wide, large);
    }

    private static string FormatStart(WidgetEventItem item, DateTime day)
        => item.Start.Date > day.Date
            ? $"{Translator.ToDoPage_DuePresetTomorrow} {item.Start.ToString("t", CultureInfo.CurrentCulture)}"
            : item.Start.ToString("t", CultureInfo.CurrentCulture);

    private static string FormatRange(WidgetEventItem item, DateTime day)
        => $"{FormatStart(item, day)} – {item.End.ToString("t", CultureInfo.CurrentCulture)}";

    private static string Tile(string medium, string wide, string large)
    {
        var builder = new StringBuilder("<tile><visual branding=\"name\">");
        builder.Append("<binding template=\"TileMedium\">").Append(medium).Append("</binding>");
        builder.Append("<binding template=\"TileWide\">").Append(wide).Append("</binding>");
        builder.Append("<binding template=\"TileLarge\">").Append(large).Append("</binding>");
        return builder.Append("</visual></tile>").ToString();
    }

    private static string TileText(string value, string style)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $"<text hint-style=\"{style}\" hint-wrap=\"false\">{SecurityElement.Escape(value)}</text>";

    // Built from markup on purpose, as the badge is: node casts are not projected under Native AOT.
    private static XmlDocument LoadTileXml(string xml)
    {
        var document = new XmlDocument();
        document.LoadXml(xml);
        return document;
    }

    private sealed record TileFrame(string Tag, string Xml, DateTimeOffset Expires);

    private sealed record ScheduledTileFrame(string Xml, DateTimeOffset DeliveryTime);
}
