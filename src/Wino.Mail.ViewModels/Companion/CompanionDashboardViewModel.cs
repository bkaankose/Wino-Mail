#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Companion;

/// <summary>
/// The companion ("Wino at a glance") dashboard shared by the Windows tray flyout and the macOS
/// menu bar popover: next events, unread mail, My Day tasks, favorite contacts and the
/// notification snooze. Section visibility is exposed as plain booleans; each host maps them to
/// its own visibility type.
/// </summary>
public partial class CompanionDashboardViewModel : ObservableObject, IDisposable
{
    public const int MaximumMail = 3;
    public const int MaximumTasks = 3;
    public const int MaximumContacts = 5;
    public const int MaximumEvents = 5;

    /// <summary>Messages are coalesced for this long before the content reloads.</summary>
    public static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IServiceProvider _services;
    private readonly ICompanionActionHandler _actions;
    private readonly IMessenger _messenger;
    private readonly IDispatcher _dispatcher;
    private readonly DateTimeOffset _sessionStartedAtUtc;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _visibilityCancellation;
    private CancellationTokenSource? _debounceCancellation;
    private DateTimeOffset _loadedAt;
    private long _generation;
    private bool _recipientsRegistered;
    private bool _disposed;
    private bool _showCalendar;
    private bool _showUnreadMail;
    private bool _showTasks;
    private bool _showFavoriteContacts;

    public CompanionDashboardViewModel(
        IServiceProvider services,
        ICompanionActionHandler actions,
        IDispatcher dispatcher,
        DateTimeOffset sessionStartedAtUtc)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sessionStartedAtUtc = sessionStartedAtUtc;
        _messenger = services.GetRequiredService<IMessenger>();
    }

    /// <summary>Raised after a navigation command succeeded, so the host can close its surface.</summary>
    public event EventHandler? NavigationCompleted;

    /// <summary>Raised after the content was reloaded (successfully or not). Hosts may resize.</summary>
    public event EventHandler? ContentRefreshed;

    public ObservableCollection<MailItemViewModel> UnreadMail { get; } = [];
    public ObservableCollection<CalendarItemViewModel> LaterEvents { get; } = [];
    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];
    public ObservableCollection<AccountContactViewModel> Favorites { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitializing))]
    [NotifyPropertyChangedFor(nameof(IsNoAccounts))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    public partial CompanionSurfaceState SurfaceState { get; set; } = CompanionSurfaceState.Initializing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEvent))]
    public partial CalendarItemViewModel? NextEvent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadTotalText))]
    public partial int UnreadTotal { get; set; }

    [ObservableProperty]
    public partial string GreetingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnoozeGlyph))]
    [NotifyPropertyChangedFor(nameof(SnoozeActionText))]
    [NotifyPropertyChangedFor(nameof(SnoozeInfoText))]
    public partial bool SnoozeNotifications { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnoozeInfoText))]
    [NotifyPropertyChangedFor(nameof(CustomSnoozeText))]
    [NotifyPropertyChangedFor(nameof(IsCustomSnoozeVisible))]
    public partial DateTimeOffset? SnoozedUntil { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSnoozeVisible))]
    public partial bool IsCustomSnoozeActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    public partial string NewTaskTitle { get; set; } = string.Empty;

    public bool IsInitializing => SurfaceState == CompanionSurfaceState.Initializing;
    public bool IsNoAccounts => SurfaceState == CompanionSurfaceState.NoAccounts;
    public bool IsReady => SurfaceState == CompanionSurfaceState.Ready;
    public bool IsUnavailable => SurfaceState == CompanionSurfaceState.Unavailable;
    public bool HasEvent => NextEvent is not null;
    public bool HasLaterEvents => LaterEvents.Count > 0;
    public bool HasUnreadMail => UnreadMail.Count > 0;
    public bool HasTasks => Tasks.Count > 0;
    public bool HasFavorites => Favorites.Count > 0;
    public bool HasPersonalizedContent => _showCalendar || _showUnreadMail || _showTasks || _showFavoriteContacts;
    public bool IsAllCaughtUp => HasPersonalizedContent && !HasEvent && !HasUnreadMail && !HasTasks;

    /// <summary>True when the scrolling event/mail/task area has anything to show.</summary>
    public bool HasContent => HasEvent || HasUnreadMail || HasTasks;
    public bool ShowEventMailSeparator => HasEvent && (HasUnreadMail || HasTasks);
    public bool ShowMailTaskSeparator => HasUnreadMail && HasTasks;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public string UnreadTotalText => UnreadTotal > 99 ? "99+" : UnreadTotal.ToString();
    public string LaterEventsText => string.Format(Translator.Companion_MoreEventsFormat, LaterEvents.Count);
    public string TaskProgressText => string.Format(
        Translator.Companion_TaskProgressFormat,
        Tasks.Count(static task => task.IsCompleted),
        Tasks.Count);
    public string SnoozeGlyph => SnoozeNotifications ? WinoIconGlyphs.GetGlyph(WinoIconGlyph.AlertOff) : WinoIconGlyphs.GetGlyph(WinoIconGlyph.Alert);
    public string SnoozeActionText => SnoozeNotifications
        ? Translator.Companion_ResumeNotifications
        : Translator.Companion_SnoozeNotifications;

    /// <summary>
    /// Text for the strip under the header. The snooze button is icon only, so this is where the
    /// user actually learns notifications are held, and until when. Shown while
    /// <see cref="SnoozeNotifications"/> is true.
    /// </summary>
    public string SnoozeInfoText
    {
        get
        {
            if (!SnoozeNotifications)
                return string.Empty;

            return SnoozedUntil is { } until
                ? string.Format(Translator.NotificationSettings_State_SnoozedUntilFormat, until.ToString("t"))
                : Translator.Companion_NotificationsSnoozed;
        }
    }

    /// <summary>
    /// The dropdown carries fixed presets only, so a custom snooze picked in Settings would otherwise
    /// be invisible here. This transient entry shows it, and goes away once it ends or is replaced.
    /// </summary>
    public string CustomSnoozeText => SnoozedUntil is { } until
        ? string.Format(Translator.NotificationSnooze_CustomUntilFormat, until.ToString("t"))
        : string.Empty;

    public bool IsCustomSnoozeVisible => IsCustomSnoozeActive && SnoozedUntil is not null;

    public string NextEventTimeText => NextEvent is null
        ? string.Empty
        : string.Format(
            Translator.Companion_EventTimeFormat,
            NextEvent.StartDate.ToString("t"),
            NextEvent.EndDate.ToString("t"));
    public string NextEventCountdownText
    {
        get
        {
            if (NextEvent is null)
                return string.Empty;

            var minutes = (int)Math.Round((NextEvent.StartDate - _loadedAt.LocalDateTime).TotalMinutes);
            if (minutes <= 0)
                return Translator.Companion_EventNow;

            return minutes < 60
                ? string.Format(Translator.Companion_EventInMinutesFormat, minutes)
                : NextEvent.StartDate.ToString("t");
        }
    }
    public string NextEventAttendeesText => NextEvent is null || NextEvent.Attendees.Count == 0
        ? string.Empty
        : string.Format(Translator.Companion_EventAttendeesFormat, NextEvent.Attendees.Count);
    public string NextEventCalendarName => NextEvent?.AssignedCalendar?.Name ?? string.Empty;
    public string NextEventCalendarColorHex
    {
        get
        {
            var customColor = NextEvent?.CalendarItem.CustomEventColorHex;
            return !string.IsNullOrWhiteSpace(customColor)
                ? customColor
                : NextEvent?.AssignedCalendar?.BackgroundColorHex ?? string.Empty;
        }
    }
    public string NextEventPrimaryActionText => NextEvent is not null && HasJoinUri(NextEvent)
        ? Translator.Companion_Join
        : Translator.Companion_View;
    public bool HasNextEventLocation => !string.IsNullOrWhiteSpace(NextEvent?.Location);
    public bool IsNextEventOnline => NextEvent is not null && HasJoinUri(NextEvent);
    public bool HasNextEventCalendar => !string.IsNullOrWhiteSpace(NextEventCalendarName);
    public bool HasNextEventAttendees => NextEvent?.Attendees.Count > 0;

    /// <summary>True between <see cref="OpenAsync"/> and <see cref="Close"/>.</summary>
    public bool IsOpen => _recipientsRegistered;

    /// <summary>Loads the surface for the host's current readiness and follows changes until <see cref="Close"/>.</summary>
    public async Task OpenAsync(CompanionReadinessState readiness, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _visibilityCancellation?.Cancel();
        _visibilityCancellation?.Dispose();
        _visibilityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterRecipients();

        switch (readiness)
        {
            case CompanionReadinessState.NoAccounts:
                ClearContent();
                SurfaceState = CompanionSurfaceState.NoAccounts;
                break;
            case CompanionReadinessState.Ready:
                await RefreshAsync(_visibilityCancellation.Token);
                break;
            default:
                ClearContent();
                SurfaceState = CompanionSurfaceState.Initializing;
                break;
        }
    }

    public void Close()
    {
        _generation++;
        _debounceCancellation?.Cancel();
        _visibilityCancellation?.Cancel();
        UnregisterRecipients();
    }

    public void ReportActionError(Exception exception)
        => ErrorText = exception.Message;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        SurfaceState = CompanionSurfaceState.Initializing;
        await _refreshGate.WaitAsync(cancellationToken);

        try
        {
            var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (accounts.Count == 0)
            {
                await RunOnUIAsync(() =>
                {
                    ClearContent();
                    SurfaceState = CompanionSurfaceState.NoAccounts;
                });
                return;
            }

            var preferences = _services.GetRequiredService<IPreferencesService>();
            _showCalendar = preferences.ShowCalendarInCompanion;
            _showUnreadMail = preferences.ShowUnreadMailInCompanion;
            _showTasks = preferences.ShowTasksInCompanion;
            _showFavoriteContacts = preferences.ShowFavoriteContactsInCompanion;

            var folders = _showUnreadMail || _showFavoriteContacts
                ? await LoadCountedFoldersAsync(cancellationToken)
                : [];
            var (mail, unreadTotal) = _showUnreadMail
                ? await LoadMailAsync(folders, preferences, cancellationToken)
                : ([], 0);
            var tasks = _showTasks ? await LoadTasksAsync(cancellationToken) : [];
            var contacts = _showFavoriteContacts ? await LoadContactsAsync(folders, cancellationToken) : [];
            var events = _showCalendar ? await LoadEventsAsync(accounts, cancellationToken) : [];
            var loadedAt = DateTimeOffset.Now;

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation)
                return;

            await RunOnUIAsync(() => ApplyResults(mail, tasks, contacts, events, unreadTotal, loadedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUIAsync(() =>
            {
                ClearContent();
                ErrorText = ex.Message;
                SurfaceState = CompanionSurfaceState.Unavailable;
            });
        }
        finally
        {
            _refreshGate.Release();
            ContentRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<List<MailItemFolder>> LoadCountedFoldersAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _services.GetRequiredService<IUnreadBadgeService>().GetSnapshotAsync();
        var ids = snapshot.Accounts
            .SelectMany(static account => account.Contributions)
            .Select(static contribution => contribution.FolderId)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return [];

        return await _services.GetRequiredService<IFolderService>()
            .GetFoldersByIdsAsync(ids, cancellationToken);
    }

    private async Task<(List<MailItemViewModel> Mail, int Count)> LoadMailAsync(
        IReadOnlyList<MailItemFolder> folders,
        IPreferencesService preferences,
        CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
            return ([], 0);

        IReadOnlyList<IMailItemFolder> sources = folders.Cast<IMailItemFolder>().ToArray();
        var options = new MailListInitializationOptions(
            sources,
            FilterOptionType.Unread,
            SortingOptionType.ReceiveDate,
            CreateThreads: false,
            IsFocusedOnly: null,
            SearchQuery: null,
            DeduplicateByServerId: true,
            Take: MaximumMail)
        {
            ExcludeDrafts = true,
            ReceivedAfterUtc = preferences.CompanionUnreadMessageBehavior == CompanionUnreadMessageBehavior.AfterAppSession
                ? _sessionStartedAtUtc
                : null
        };
        var mailService = _services.GetRequiredService<IMailService>();
        var mails = await mailService.FetchMailsAsync(options, cancellationToken);
        var count = await mailService.CountMailsAsync(options, cancellationToken);

        var viewModels = mails
            .Take(MaximumMail)
            .Select(mail => new MailItemViewModel(mail, preferences.AccountNicknamePosition))
            .ToList();

        return (viewModels, count);
    }

    private async Task<List<TaskItemViewModel>> LoadTasksAsync(CancellationToken cancellationToken)
    {
        var taskService = _services.GetRequiredService<ITaskQueryService>();
        var tasks = await taskService.GetTasksAsync(view: TaskViewKind.MyDay, sort: TaskSortKind.Importance);
        var lists = await taskService.GetTaskListsAsync();
        var names = lists.ToDictionary(static list => list.Id, static list => list.Title ?? string.Empty);
        cancellationToken.ThrowIfCancellationRequested();

        return tasks
            .OrderBy(static task => task.IsCompleted)
            .ThenByDescending(static task => task.IsImportant)
            .ThenBy(static task => task.DueDate ?? DateTime.MaxValue)
            .ThenByDescending(static task => task.ModifiedAtUtc)
            .Take(MaximumTasks)
            .Select(task => new TaskItemViewModel(
                task,
                names.TryGetValue(task.TaskListId, out var name) ? name : string.Empty)
            {
                ShowListName = true
            })
            .ToList();
    }

    private async Task<List<AccountContactViewModel>> LoadContactsAsync(
        IReadOnlyList<MailItemFolder> folders,
        CancellationToken cancellationToken)
    {
        var preferences = _services.GetRequiredService<IPreferencesService>();
        var query = await _services.GetRequiredService<IContactQueryService>().GetContactsPageAsync(
            new ContactQueryFilter(FavoritesOnly: true),
            0,
            MaximumContacts,
            preferences.ContactSortOrder);
        var addresses = query.Contacts
            .Select(static contact => contact.PrimaryEmailAddress)
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .ToArray();
        var unread = await _services.GetRequiredService<IMailService>().GetUnreadSenderCountsAsync(
            folders.Select(static folder => folder.Id).ToArray(),
            addresses!,
            cancellationToken);
        var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
        var accountNames = accounts.ToDictionary(static account => account.Id, static account => account.Name ?? string.Empty);

        return query.Contacts.Select(contact =>
        {
            var viewModel = new AccountContactViewModel(
                contact,
                accountNames.TryGetValue(contact.MailAccountId, out var name) ? name : string.Empty,
                displayFormat: preferences.ContactNameDisplayFormat,
                sortOrder: preferences.ContactSortOrder);
            var normalized = Wino.Core.Domain.Entities.Shared.ContactEmailAddress.Normalize(contact.PrimaryEmailAddress);
            viewModel.UnreadCount = normalized is not null && unread.TryGetValue(normalized, out var count) ? count : 0;
            return viewModel;
        }).ToList();
    }

    private async Task<List<CalendarItemViewModel>> LoadEventsAsync(
        IReadOnlyList<Wino.Core.Domain.Entities.Shared.MailAccount> accounts,
        CancellationToken cancellationToken)
    {
        var calendarService = _services.GetRequiredService<ICalendarService>();
        var now = DateTime.Now;
        var end = now.Date.AddDays(1);
        var period = new TimeRange(now, end);
        var events = new List<CalendarItemViewModel>();

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calendars = await calendarService.GetAccountCalendarsAsync(account.Id);
            foreach (var calendar in calendars.Where(static calendar => calendar.IsPrimary))
            {
                var items = await calendarService.GetCalendarEventsAsync(calendar, period);
                foreach (var item in items.Where(item =>
                             !item.IsAllDayEvent &&
                             item.LocalEndDate > now &&
                             item.LocalStartDate < end))
                {
                    var attendees = await calendarService.GetAttendeesAsync(item.EventTrackingId);
                    var isDeclined = attendees.Any(attendee =>
                        attendee.AttendenceStatus == AttendeeStatus.Declined &&
                        string.Equals(attendee.Email?.Trim(), account.Address?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (isDeclined)
                        continue;

                    var viewModel = new CalendarItemViewModel(item) { DisplayingPeriod = period };
                    foreach (var attendee in attendees)
                        viewModel.Attendees.Add(attendee);
                    events.Add(viewModel);
                }
            }
        }

        return events
            .OrderBy(static item => item.StartDate)
            .ThenBy(static item => item.Id)
            .Take(MaximumEvents)
            .ToList();
    }

    private void ApplyResults(
        IReadOnlyList<MailItemViewModel> mail,
        IReadOnlyList<TaskItemViewModel> tasks,
        IReadOnlyList<AccountContactViewModel> contacts,
        IReadOnlyList<CalendarItemViewModel> events,
        int unreadTotal,
        DateTimeOffset loadedAt)
    {
        _loadedAt = loadedAt;
        Replace(UnreadMail, mail);
        Replace(Tasks, tasks);
        Replace(Favorites, contacts);
        NextEvent = events.FirstOrDefault();
        Replace(LaterEvents, events.Skip(1));
        UnreadTotal = unreadTotal;
        RefreshSnoozeState();
        GreetingText = loadedAt.Hour switch
        {
            < 12 => Translator.Companion_GreetingMorning,
            < 18 => Translator.Companion_GreetingAfternoon,
            _ => Translator.Companion_GreetingEvening
        };
        DateText = loadedAt.ToString("D");
        ErrorText = string.Empty;
        RaiseContentProperties();
        SurfaceState = CompanionSurfaceState.Ready;
    }

    private void ClearContent()
    {
        UnreadMail.Clear();
        Tasks.Clear();
        Favorites.Clear();
        LaterEvents.Clear();
        NextEvent = null;
        UnreadTotal = 0;
        RaiseContentProperties();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private void RaiseContentProperties()
    {
        OnPropertyChanged(nameof(HasLaterEvents));
        OnPropertyChanged(nameof(HasUnreadMail));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(IsAllCaughtUp));
        OnPropertyChanged(nameof(HasPersonalizedContent));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowEventMailSeparator));
        OnPropertyChanged(nameof(ShowMailTaskSeparator));
        OnPropertyChanged(nameof(LaterEventsText));
        OnPropertyChanged(nameof(TaskProgressText));
        RaiseNextEventProperties();
    }

    partial void OnNextEventChanged(CalendarItemViewModel? value) => RaiseNextEventProperties();

    private void RaiseNextEventProperties()
    {
        OnPropertyChanged(nameof(NextEventTimeText));
        OnPropertyChanged(nameof(NextEventCountdownText));
        OnPropertyChanged(nameof(NextEventAttendeesText));
        OnPropertyChanged(nameof(NextEventCalendarName));
        OnPropertyChanged(nameof(NextEventCalendarColorHex));
        OnPropertyChanged(nameof(NextEventPrimaryActionText));
        OnPropertyChanged(nameof(HasNextEventLocation));
        OnPropertyChanged(nameof(IsNextEventOnline));
        OnPropertyChanged(nameof(HasNextEventCalendar));
        OnPropertyChanged(nameof(HasNextEventAttendees));
    }

    private void RegisterRecipients()
    {
        if (_recipientsRegistered)
            return;

        _recipientsRegistered = true;
        _messenger.Register<CompanionDashboardViewModel, MailAddedMessage>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, MailUpdatedMessage>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, MailRemovedMessage>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, BulkMailAddedMessage>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, BulkMailUpdatedMessage>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, BulkMailRemovedMessage>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarItemAdded>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarItemUpdated>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarItemDeleted>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, TaskSynchronizationCompleted>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, ContactSynchronizationCompleted>(this, static (recipient, _) => recipient.ScheduleRefresh());

        // The snooze can also be changed from the settings page, or cleared by expiry, so follow
        // the preference rather than only re-reading it on the next content refresh.
        _services.GetRequiredService<IPreferencesService>().PreferenceChanged += OnPreferenceChanged;
    }

    private void UnregisterRecipients()
    {
        if (!_recipientsRegistered)
            return;

        _recipientsRegistered = false;
        _messenger.UnregisterAll(this);
        _services.GetRequiredService<IPreferencesService>().PreferenceChanged -= OnPreferenceChanged;
    }

    private void OnPreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName is nameof(IPreferencesService.NotificationSnoozePreset)
            or nameof(IPreferencesService.NotificationSnoozeUntilUtcTicks))
        {
            // The write can come from a background sync thread, so hop before touching bound state.
            _ = RunOnUIAsync(RefreshSnoozeState);
            return;
        }

        // Section toggles in Settings apply while the companion is open.
        if (propertyName is nameof(IPreferencesService.ShowCalendarInCompanion)
            or nameof(IPreferencesService.ShowUnreadMailInCompanion)
            or nameof(IPreferencesService.ShowTasksInCompanion)
            or nameof(IPreferencesService.ShowFavoriteContactsInCompanion)
            or nameof(IPreferencesService.CompanionUnreadMessageBehavior))
        {
            ScheduleRefresh();
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed || _visibilityCancellation is null)
            return;

        _debounceCancellation?.Cancel();
        _debounceCancellation?.Dispose();
        _debounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_visibilityCancellation.Token);
        var token = _debounceCancellation.Token;
        _ = DebounceRefreshAsync(token);
    }

    private async Task DebounceRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RefreshDebounce, cancellationToken).ConfigureAwait(false);
            await RunOnUIAsync(() => _ = RefreshFromUIAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUIAsync(() =>
            {
                ErrorText = ex.Message;
                SurfaceState = CompanionSurfaceState.Unavailable;
            });
        }
    }

    private async Task RefreshFromUIAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            SurfaceState = CompanionSurfaceState.Unavailable;
        }
    }

    [RelayCommand]
    private Task OpenWinoAsync() => ExecuteNavigationAsync(_actions.OpenWinoAsync);

    [RelayCommand]
    private Task OpenCalendarAsync() => ExecuteNavigationAsync(_actions.OpenCalendarAsync);

    [RelayCommand]
    private Task OpenTasksAsync() => ExecuteNavigationAsync(_actions.OpenTasksAsync);

    [RelayCommand]
    private Task OpenInboxAsync() => ExecuteNavigationAsync(token => _actions.OpenInboxAsync(null, token));

    [RelayCommand]
    private Task OpenMailAsync(MailItemViewModel mail)
        => ExecuteNavigationAsync(token => _actions.OpenMailAsync(mail, token));

    [RelayCommand]
    private Task OpenCalendarEventAsync(CalendarItemViewModel calendarItem)
        => ExecuteNavigationAsync(token => _actions.OpenCalendarEventAsync(calendarItem, token));

    [RelayCommand]
    private Task OpenPrimaryEventAsync(CalendarItemViewModel calendarItem)
        => ExecuteNavigationAsync(token => HasJoinUri(calendarItem)
            ? _actions.JoinCalendarEventAsync(calendarItem, token)
            : _actions.OpenCalendarEventAsync(calendarItem, token));

    [RelayCommand]
    private Task FindContactAsync(AccountContactViewModel contact)
        => ExecuteNavigationAsync(token => _actions.FindContactAsync(contact, token));

    [RelayCommand]
    private Task FindAnyContactAsync()
        => ExecuteNavigationAsync(token => _actions.FindContactAsync(null, token));

    [RelayCommand]
    private Task NewMailAsync() => ExecuteNavigationAsync(_actions.NewMailAsync);

    [RelayCommand]
    private Task NewEventAsync() => ExecuteNavigationAsync(_actions.NewEventAsync);

    [RelayCommand]
    private Task OpenSettingsAsync() => ExecuteNavigationAsync(_actions.OpenSettingsAsync);

    /// <summary>
    /// Re-reads snooze state from the policy service, which also clears an elapsed snooze.
    /// </summary>
    public void RefreshSnoozeState()
    {
        var state = _services.GetRequiredService<INotificationPolicyService>().GetSnoozeState(DateTimeOffset.Now);

        SnoozeNotifications = state.IsSnoozed;
        SnoozedUntil = state.Until;
        IsCustomSnoozeActive = state.IsSnoozed && state.Preset == NotificationSnoozePreset.Custom;
    }

    [RelayCommand]
    private async Task StartNotificationSnoozeAsync(NotificationSnoozePreset preset)
    {
        var previousSnoozed = SnoozeNotifications;
        var previousUntil = SnoozedUntil;

        SnoozeNotifications = true;

        try
        {
            await _actions.StartNotificationSnoozeAsync(preset, CurrentToken);
            RefreshSnoozeState();
        }
        catch (Exception ex)
        {
            SnoozeNotifications = previousSnoozed;
            SnoozedUntil = previousUntil;
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ResumeNotificationsAsync()
    {
        var previousSnoozed = SnoozeNotifications;
        var previousUntil = SnoozedUntil;

        SnoozeNotifications = false;

        try
        {
            await _actions.ResumeNotificationsAsync(CurrentToken);
            RefreshSnoozeState();
        }
        catch (Exception ex)
        {
            SnoozeNotifications = previousSnoozed;
            SnoozedUntil = previousUntil;
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task MarkMailReadAsync(MailItemViewModel mail)
    {
        var previous = mail.IsRead;
        mail.IsRead = true;
        try
        {
            await _actions.SetMailReadAsync(mail, true, CurrentToken);
            UnreadMail.Remove(mail);
            RaiseContentProperties();
        }
        catch (Exception ex)
        {
            mail.IsRead = previous;
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ArchiveMailAsync(MailItemViewModel mail)
    {
        var index = UnreadMail.IndexOf(mail);
        if (index >= 0)
            UnreadMail.RemoveAt(index);
        RaiseContentProperties();

        try
        {
            await _actions.ArchiveMailAsync(mail, CurrentToken);
        }
        catch (Exception ex)
        {
            if (index >= 0)
                UnreadMail.Insert(Math.Min(index, UnreadMail.Count), mail);
            ErrorText = ex.Message;
            RaiseContentProperties();
        }
    }

    [RelayCommand]
    private async Task SetTaskCompletedAsync(TaskItemViewModel task)
    {
        var previous = task.IsCompleted;
        task.IsCompleted = !previous;
        try
        {
            await _actions.SetTaskCompletedAsync(task, task.IsCompleted, CurrentToken);
        }
        catch (Exception ex)
        {
            task.IsCompleted = previous;
            ErrorText = ex.Message;
        }

        OnPropertyChanged(nameof(TaskProgressText));
    }

    private bool CanAddTask() => !string.IsNullOrWhiteSpace(NewTaskTitle);

    [RelayCommand(CanExecute = nameof(CanAddTask))]
    private async Task AddTaskAsync()
    {
        var title = NewTaskTitle;
        NewTaskTitle = string.Empty;

        try
        {
            await _actions.CreateMyDayTaskAsync(title, CurrentToken);
            await RefreshAsync(CurrentToken);
        }
        catch (Exception ex)
        {
            NewTaskTitle = title;
            ErrorText = ex.Message;
        }
    }

    private async Task ExecuteNavigationAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CurrentToken);
            NavigationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (CurrentToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private CancellationToken CurrentToken => _visibilityCancellation?.Token ?? CancellationToken.None;

    private static bool HasJoinUri(CalendarItemViewModel? item)
        => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item?.CalendarItem, out _);

    private Task RunOnUIAsync(Action action) => _dispatcher.ExecuteOnUIThread(action);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
        _debounceCancellation?.Dispose();
        _visibilityCancellation?.Dispose();
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
