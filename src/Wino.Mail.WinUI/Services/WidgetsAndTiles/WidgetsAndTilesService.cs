using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Storage;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Notifications;
using Wino.Mail.ViewModels.Companion;
using Wino.NotificationHost.Contracts;
using Wino.Services.Logging;

namespace Wino.Mail.WinUI.Services.WidgetsAndTiles;

/// <summary>
/// Shows the companion's content on the Widgets Board (Windows 11), through the snapshot Wino.WidgetHost renders, and
/// on the Wino Mail Live Tile (Windows 10). The app requests updates when content changes; requests are coalesced
/// and run on the thread pool, and no failure reaches the caller. With both surfaces off, nothing is loaded.
/// </summary>
public sealed partial class WidgetsAndTilesService(
    CompanionContentService content,
    ContactPictureThumbnailer pictureThumbnailer,
    WindowsShellPreferences shellPreferences)
{
    private const int WindowsElevenBuild = 22000;
    private const int MaximumMail = 5;
    private const int MaximumEvents = 50;
    private const int MaximumEventsPerDay = 8;
    private const int MaximumTasks = 5;
    private const int MaximumPeople = 8;

    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(10);
    private const string UpdateFailedLogKey = "update-failed";

    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly Lock _scheduleGate = new();
    private readonly ThrottledLog _failureLog = new(TimeSpan.FromMinutes(30));
    private CancellationTokenSource? _pending;
    private DateTimeOffset _pendingSince;
    private DateTimeOffset _pendingDue;
    private Timer? _boundaryTimer;
    private string? _publishedWidgetContent;
    private bool _widgetsTurnedOffWritten;
    private TimeSpan _retryDelay = InitialRetryDelay;

    /// <summary>Live Tiles exist only on the Windows 10 Start menu. Windows 11 keeps the API but shows nothing.</summary>
    public static bool AreLiveTilesSupported { get; } = Environment.OSVersion.Version.Build < WindowsElevenBuild;

    /// <summary>The Widgets Board, and with it the widget provider, exists only on Windows 11.</summary>
    public static bool AreWidgetsSupported { get; } = Environment.OSVersion.Version.Build >= WindowsElevenBuild;

    private bool UpdatesTiles => AreLiveTilesSupported && shellPreferences.IsLiveTilesEnabled;

    private bool UpdatesWidgets => AreWidgetsSupported && shellPreferences.IsWinoWidgetsEnabled;

    /// <summary>Content changed. The update follows once changes settle, and at least every few seconds.</summary>
    public void RequestUpdate() => Schedule(Debounce, force: false);

    /// <summary>A widget or tile preference changed. Runs even when both surfaces were just turned off.</summary>
    public void RequestImmediateUpdate() => Schedule(TimeSpan.Zero, force: true);

    private void Schedule(TimeSpan delay, bool force)
    {
        if (!force && !UpdatesTiles && !UpdatesWidgets)
            return;

        CancellationTokenSource source;
        CancellationToken token;
        lock (_scheduleGate)
        {
            // A steady stream of changes, such as a first sync, still updates at least every few seconds. A pending
            // retry that is due later than this request does not hold it back.
            var now = DateTimeOffset.UtcNow;
            if (_pending != null && !force && now - _pendingSince >= MaximumDelay && _pendingDue <= now + delay)
                return;

            if (_pending == null)
                _pendingSince = now;

            _pending?.Cancel();
            _pending?.Dispose();
            _pending = source = new CancellationTokenSource();
            _pendingDue = now + delay;
            token = source.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);

                lock (_scheduleGate)
                {
                    if (ReferenceEquals(_pending, source))
                    {
                        _pending = null;
                        source.Dispose();
                    }
                }

                await UpdateAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Widgets and Live Tiles: scheduled update failed.");
            }
        });
    }

    private async Task UpdateAsync()
    {
        await _updateGate.WaitAsync().ConfigureAwait(false);

        try
        {
            var tileModes = new LiveTileModes(
                shellPreferences.ShowMailOnLiveTiles,
                shellPreferences.ShowCalendarOnLiveTiles,
                shellPreferences.ShowTasksOnLiveTiles);
            var tiles = UpdatesTiles && (tileModes.Mail || tileModes.Calendar || tileModes.Tasks);
            var widgets = UpdatesWidgets;
            var showDetails = shellPreferences.ShowDetailsInWidgetsAndTiles;

            var snapshot = tiles || widgets
                ? await LoadAsync(
                    mail: widgets || tileModes.Mail,
                    calendar: widgets || tileModes.Calendar,
                    tasks: widgets || tileModes.Tasks,
                    people: widgets,
                    showDetails).ConfigureAwait(false)
                : null;

            if (AreLiveTilesSupported)
                LogChange(UpdateLiveTile(tiles ? snapshot : null, tileModes, showDetails));

            if (AreWidgetsSupported && (widgets || !_widgetsTurnedOffWritten))
            {
                LogChange(WriteWidgetSnapshot(widgets ? snapshot! : new WidgetSnapshot { State = WidgetSnapshotState.Disabled }));
                _widgetsTurnedOffWritten = !widgets;
            }

            ScheduleBoundary(snapshot);

            if (_retryDelay != InitialRetryDelay)
            {
                _retryDelay = InitialRetryDelay;
                _failureLog.Reset(UpdateFailedLogKey);
                Log.Information("Widgets and Live Tiles: update succeeded after earlier failures.");
            }
        }
        catch (Exception ex)
        {
            var retryDelay = _retryDelay;
            _retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaximumRetryDelay.Ticks));

            if (_failureLog.ShouldWrite(UpdateFailedLogKey, out var suppressed))
                Log.Warning(ex, "Widgets and Live Tiles: update failed, retrying in {RetryDelay} ({Suppressed} more failures since the last report).",
                    retryDelay, suppressed);

            Schedule(retryDelay, force: true);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    /// <summary>
    /// Loads the content and applies privacy: an account that hides notification content, or Show details turned
    /// off, leaves only the placeholder text in an item, so neither surface can reveal it. Texts are formatted here,
    /// in the app's language and culture, so the widget host needs no translations or database.
    /// </summary>
    private async Task<WidgetSnapshot> LoadAsync(bool mail, bool calendar, bool tasks, bool people, bool showDetails)
    {
        var now = DateTime.Now;
        var accounts = await content.GetAccountsAsync().ConfigureAwait(false);
        if (accounts.Count == 0)
            return new WidgetSnapshot { State = WidgetSnapshotState.NoAccounts };

        var hiddenAccountIds = accounts
            .Where(static account => NotificationSettingsResolver.HidesNotificationContent(account.Preferences))
            .Select(static account => account.Id)
            .ToHashSet();
        bool IsHidden(Guid? accountId) => !showDetails || accountId is not { } id || hiddenAccountIds.Contains(id);

        var snapshot = new WidgetSnapshot { State = WidgetSnapshotState.Ready };

        if (mail)
        {
            var folders = await content.GetCountedFoldersAsync(CancellationToken.None).ConfigureAwait(false);
            var (messages, unreadTotal) = await content
                .GetUnreadMailAsync(folders, MaximumMail, null, CancellationToken.None)
                .ConfigureAwait(false);

            snapshot.Mail.UnreadTotal = unreadTotal;
            snapshot.Mail.Items = messages.Select(message =>
            {
                var copy = message.MailCopy;
                var received = copy.CreationDate.ToLocalTime();
                var hidden = IsHidden(copy.AssignedAccount?.Id ?? copy.AssignedFolder?.MailAccountId);
                return new WidgetMailItem
                {
                    Id = copy.UniqueId.ToString("D"),
                    Sender = hidden
                        ? Translator.LiveTiles_NewMessage
                        : string.IsNullOrWhiteSpace(copy.FromName) ? copy.FromAddress ?? string.Empty : copy.FromName,
                    Subject = hidden ? string.Empty : copy.Subject ?? string.Empty,
                    Preview = hidden ? string.Empty : copy.PreviewText ?? string.Empty,
                    Time = received.Date == now.Date
                        ? received.ToString("t", CultureInfo.CurrentCulture)
                        : received.ToString("M", CultureInfo.CurrentCulture),
                    IsHidden = hidden
                };
            }).ToList();
        }

        if (calendar)
        {
            var tomorrow = now.Date.AddDays(1);
            var events = await content
                .GetUpcomingEventsAsync(accounts, now.Date.AddDays(2), MaximumEvents, CancellationToken.None)
                .ConfigureAwait(false);

            // Per day, so a busy today never crowds tomorrow out of the large widget and the midnight tile.
            var shown = events
                .GroupBy(static item => item.StartDate.Date)
                .SelectMany(static day => day.Take(MaximumEventsPerDay))
                .ToList();

            snapshot.Calendar = new WidgetCalendarSection
            {
                DayName = now.ToString("ddd", CultureInfo.CurrentCulture),
                DayNumber = now.Day.ToString(CultureInfo.CurrentCulture),
                DateText = $"{now.ToString("ddd", CultureInfo.CurrentCulture)}, {now.ToString("M", CultureInfo.CurrentCulture)}",
                TodayCountText = string.Format(Translator.LiveTiles_EventsTodayFormat, shown.Count(item => item.StartDate < tomorrow)),
                Events = shown.Select(item =>
                {
                    var hidden = IsHidden(item.AssignedCalendar?.AccountId);
                    return new WidgetEventItem
                    {
                        Id = item.Id.ToString("D"),
                        Title = hidden ? Translator.Busy : item.Title ?? string.Empty,
                        TimeText = string.Format(
                            Translator.Companion_EventTimeFormat,
                            item.StartDate.ToString("t", CultureInfo.CurrentCulture),
                            item.EndDate.ToString("t", CultureInfo.CurrentCulture)),
                        Location = hidden ? string.Empty : item.Location ?? string.Empty,
                        ColorHex = Wino.Calendar.ViewModels.CalendarAgenda.ColorHex(item) ?? string.Empty,
                        IsOnline = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item.CalendarItem, out _),
                        IsTomorrow = item.StartDate >= tomorrow,
                        Start = new DateTimeOffset(item.StartDate),
                        End = new DateTimeOffset(item.EndDate)
                    };
                }).ToList()
            };
        }

        if (tasks)
        {
            var myDay = await content.GetMyDayTasksAsync(CancellationToken.None).ConfigureAwait(false);
            var completed = myDay.Count(static task => task.IsCompleted);

            snapshot.Tasks = new WidgetTasksSection
            {
                Completed = completed,
                Total = myDay.Count,
                ProgressText = string.Format(Translator.Companion_TaskProgressFormat, completed, myDay.Count),
                Items = myDay
                    .Where(static task => !task.IsCompleted)
                    .Take(MaximumTasks)
                    .Select(task => new WidgetTaskItem
                    {
                        Id = task.Id.ToString("D"),
                        Title = showDetails ? task.Title ?? string.Empty : Translator.LiveTiles_HiddenTask,
                        Detail = string.Join(" · ", new[] { showDetails ? task.ListName : null, task.DueDisplayText }
                            .Where(static part => !string.IsNullOrWhiteSpace(part))),
                        IsImportant = task.IsImportant
                    })
                    .ToList()
            };
        }

        if (people)
        {
            var favorites = await content.GetFavoritesAsync(MaximumPeople, null, CancellationToken.None).ConfigureAwait(false);
            foreach (var favorite in favorites)
            {
                var name = showDetails ? favorite.ShortDisplayName ?? string.Empty : string.Empty;
                snapshot.People.Add(new WidgetPersonItem
                {
                    Id = favorite.Id.ToString("D"),
                    Name = name,
                    FirstName = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? name,
                    Email = showDetails ? favorite.SourceContact.PrimaryEmailAddress ?? string.Empty : string.Empty,
                    Picture = showDetails
                        ? await pictureThumbnailer.GetAsync(favorite.ContactPictureFileId).ConfigureAwait(false)
                        : string.Empty
                });
            }
        }

        return snapshot;
    }

    /// <summary>Returns a one-line description of what changed, or null when the widgets already show this content.</summary>
    private string? WriteWidgetSnapshot(WidgetSnapshot snapshot)
    {
        snapshot.Strings = CreateWidgetStrings();

        var localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
        _publishedWidgetContent ??= ReadPublishedWidgetContent(localCachePath);

        var serialized = WidgetSnapshotStore.Serialize(snapshot);
        if (serialized == _publishedWidgetContent)
            return null;

        snapshot.GeneratedAt = DateTimeOffset.Now;
        WidgetSnapshotStore.Write(localCachePath, WidgetSnapshotStore.Serialize(snapshot));
        _publishedWidgetContent = serialized;

        return snapshot.State switch
        {
            WidgetSnapshotState.Disabled => "Widgets: turned off",
            WidgetSnapshotState.NoAccounts => "Widgets: no accounts",
            _ => $"Widgets: published mail {snapshot.Mail.UnreadTotal}/{snapshot.Mail.Items.Count}, " +
                 $"calendar {snapshot.Calendar.Events.Count}, to do {snapshot.Tasks.Items.Count}, people {snapshot.People.Count}"
        };
    }

    /// <summary>The content already on disk, so a launch that changes nothing writes and logs nothing.</summary>
    private static string ReadPublishedWidgetContent(string localCachePath)
    {
        var published = WidgetSnapshotStore.TryRead(localCachePath);
        if (published == null)
            return string.Empty;

        published.GeneratedAt = default;
        return WidgetSnapshotStore.Serialize(published);
    }

    private static WidgetStrings CreateWidgetStrings() => new()
    {
        Unread = Translator.Widgets_Unread,
        NewMail = Translator.MenuNewMail,
        OpenInbox = Translator.Companion_OpenInbox,
        NoUnreadMail = Translator.Widgets_NoUnreadMail,
        NewEvent = Translator.Widgets_NewEvent,
        Join = Translator.Companion_Join,
        Today = Translator.Today,
        Tomorrow = Translator.ToDoPage_DuePresetTomorrow,
        NoUpcomingEvents = Translator.Widgets_NoUpcomingEvents,
        StartsInFormat = Translator.Companion_EventInMinutesFormat,
        Now = Translator.Companion_EventNow,
        MyDay = Translator.ToDoPage_MyDay,
        AddTask = Translator.ToDoPage_AddTask,
        NoTasks = Translator.Widgets_NoTasks,
        Favorites = Translator.Widgets_Favorites,
        NoFavorites = Translator.Widgets_NoFavorites,
        NoAccountsMail = Translator.Widgets_NoAccounts_Mail,
        NoAccountsCalendar = Translator.Widgets_NoAccounts_Calendar,
        NoAccountsTasks = Translator.Widgets_NoAccounts_Tasks,
        NoAccountsPeople = Translator.Widgets_NoAccounts_People,
        NoAccountsShort = Translator.Widgets_NoAccounts_Short,
        OpenWino = Translator.Companion_OpenWino,
        Disabled = Translator.Widgets_Disabled,
        DisabledShort = Translator.Widgets_DisabledShort,
        EnableWidgets = Translator.Widgets_EnableWidgets
    };

    /// <summary>The calendar names the next event and the day, so it is refreshed when that event ends or the day changes.</summary>
    private void ScheduleBoundary(WidgetSnapshot? snapshot)
    {
        _boundaryTimer?.Dispose();
        _boundaryTimer = null;
        if (snapshot is not { State: WidgetSnapshotState.Ready })
            return;

        var now = DateTimeOffset.Now;
        var midnight = new DateTimeOffset(now.Date.AddDays(1).AddMinutes(1));
        var nextEnd = snapshot.Calendar.Events.Select(static item => item.End).Where(end => end > now).DefaultIfEmpty(midnight).Min();
        var due = nextEnd < midnight ? nextEnd : midnight;
        _boundaryTimer = new Timer(OnBoundaryReached, this, due - now, Timeout.InfiniteTimeSpan);
    }

    private static void OnBoundaryReached(object? state)
    {
        try
        {
            if (state is WidgetsAndTilesService service)
                service.RequestUpdate();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Widgets and Live Tiles: boundary refresh failed.");
        }
    }

    private static void LogChange(string? change)
    {
        if (change != null)
            Log.Information(change);
    }
}
