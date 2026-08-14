#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.AI.Abstractions;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.ViewModels;

public sealed partial class DailyBriefingDateItem : ObservableObject
{
    public required DateOnly Date { get; init; }
    public required string DisplayName { get; init; }
    public required string SecondaryName { get; init; }
}

/// <summary>One local briefing-fact row prepared for the panel's XAML template.</summary>
public sealed partial class DailyBriefingDisplayItem : ObservableObject
{
    public required string Headline { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string When { get; init; } = string.Empty;
    public string DueText { get; init; } = string.Empty;
    public string CategoryText { get; init; } = string.Empty;
    public string CategoryGlyph { get; init; } = string.Empty;
    public DailyBriefingTone Tone { get; init; } = DailyBriefingTone.Neutral;
    public string UrgencyText { get; init; } = string.Empty;
    public bool IsPriority { get; init; }
    public Guid? MailUniqueId { get; init; }
    public string VerificationCode { get; init; } = string.Empty;
    public CalendarEventComposeNavigationArgs? CalendarArgs { get; init; }
    public required DailyBriefingActionPresentation Action { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string AccountInitials { get; init; } = string.Empty;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasSource => !string.IsNullOrWhiteSpace(Source);
    public bool HasDue => !string.IsNullOrWhiteSpace(DueText);
    public bool HasCategory => !string.IsNullOrWhiteSpace(CategoryText);
    public bool HasUrgency => !string.IsNullOrWhiteSpace(UrgencyText);
    public bool CanOpen => MailUniqueId is not null;
    public bool ShowOpenAction => CanOpen && Action.Execution != DailyBriefingActionExecution.OpenSource;

    [ObservableProperty] public partial bool IsAccountVisible { get; set; }
}

public sealed partial class DailyBriefingAccountGroup : ObservableObject
{
    public required DailyBriefingAccount Account { get; init; }
    public string AccountName => Account.Account.Name;
    public string AccountInitials => DailyBriefingPanelViewModel.GetInitials(Account.Account.Name);
    public ObservableCollection<DailyBriefingDisplayItem> Items { get; } = [];
}

public sealed partial class DailyBriefingPanelViewModel : ObservableObject, IDisposable
{
    private const int DateCount = 7;

    private readonly ILocalIntelligenceService _localService;
    private readonly IClipboardService _clipboardService;
    private readonly IDateContextProvider _dateContext;
    private readonly IDispatcher _dispatcher;
    private readonly IPreferencesService _preferencesService;
    private readonly INavigationService _navigationService;
    private readonly IMailService _mailService;
    private readonly IMimeFileService _mimeFileService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly IMailDialogService _dialogService;

    private CancellationTokenSource? _loadCancellation;
    private IReadOnlyList<DailyBriefingAccount> _eligibleAccounts = [];
    private bool _isInitialized;

    public ObservableCollection<DailyBriefingDateItem> Dates { get; } = [];
    public ObservableCollection<DailyBriefingAccountGroup> AccountGroups { get; } = [];
    public ObservableCollection<DailyBriefingDisplayItem> FlatItems { get; } = [];

    [ObservableProperty] public partial int SelectedDateIndex { get; set; }
    [ObservableProperty] public partial bool IsGrouped { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool IsUnavailable { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string LoadError { get; set; } = string.Empty;

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);
    public bool ShowContent => !IsLoading && !IsUnavailable && !IsEmpty && !HasLoadError;

    public DailyBriefingPanelViewModel(ILocalIntelligenceService localService,
        IClipboardService clipboardService,
        IDateContextProvider dateContext,
        IDispatcher dispatcher,
        IPreferencesService preferencesService,
        INavigationService navigationService,
        IMailService mailService,
        IMimeFileService mimeFileService,
        IWinoRequestDelegator requestDelegator,
        IMailDialogService dialogService)
    {
        _localService = localService;
        _clipboardService = clipboardService;
        _dateContext = dateContext;
        _dispatcher = dispatcher;
        _preferencesService = preferencesService;
        _navigationService = navigationService;
        _mailService = mailService;
        _mimeFileService = mimeFileService;
        _requestDelegator = requestDelegator;
        _dialogService = dialogService;
        IsGrouped = preferencesService.IsDailyBriefingGroupedByAccount;
    }

    /// <summary>Raised when an action needs the shell to close the panel and reveal mail or Calendar.</summary>
    public event EventHandler? CloseRequested;

    public async Task InitializeAsync()
    {
        if (Dates.Count == 0) BuildDates();

        _isInitialized = false;
        await _dispatcher.ExecuteOnUIThread(() => SelectedDateIndex = 0).ConfigureAwait(false);
        _isInitialized = true;

        await LoadAsync(refreshAccounts: true).ConfigureAwait(false);
    }

    private void BuildDates()
    {
        var today = _dateContext.GetToday();
        for (var offset = 0; offset < DateCount; offset++)
        {
            var date = today.AddDays(-offset);
            var moment = date.ToDateTime(TimeOnly.MinValue);
            Dates.Add(new()
            {
                Date = date,
                DisplayName = offset switch
                {
                    0 => Translator.DailyBriefing_Today,
                    1 => Translator.DailyBriefing_Yesterday,
                    _ => moment.ToString("dddd", _dateContext.Culture),
                },
                SecondaryName = moment.ToString("d MMMM yyyy", _dateContext.Culture),
            });
        }
    }

    partial void OnSelectedDateIndexChanged(int value)
    {
        if (!_isInitialized) return;
        _ = LoadAsync(refreshAccounts: false);
    }

    partial void OnIsGroupedChanged(bool value)
    {
        _preferencesService.IsDailyBriefingGroupedByAccount = value;
        foreach (var item in FlatItems) item.IsAccountVisible = !value;
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowContent));
    partial void OnIsUnavailableChanged(bool value) => OnPropertyChanged(nameof(ShowContent));
    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowContent));
    partial void OnLoadErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowContent));
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync(refreshAccounts: true);

    private void CancelPendingWork()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private async Task LoadAsync(bool refreshAccounts)
    {
        CancelPendingWork();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;

        await _dispatcher.ExecuteOnUIThread(() =>
        {
            AccountGroups.Clear();
            FlatItems.Clear();
            IsLoading = true;
            IsEmpty = false;
            IsUnavailable = false;
            LoadError = string.Empty;
        }).ConfigureAwait(false);

        try
        {
            if (refreshAccounts)
            {
                _eligibleAccounts = await _localService.GetEligibleAccountsAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await _localService.MarkOpenedAsync(token).ConfigureAwait(false);
            }

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var account in _eligibleAccounts)
                    AccountGroups.Add(new() { Account = account });
                IsUnavailable = _eligibleAccounts.Count == 0;
            }).ConfigureAwait(false);

            if (_eligibleAccounts.Count == 0 || SelectedDateIndex < 0 || SelectedDateIndex >= Dates.Count)
                return;

            var selectedDate = Dates[SelectedDateIndex].Date;
            var facts = await _localService.GetBriefingFactsAsync(selectedDate, _dateContext.TimeZone, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (token.IsCancellationRequested) return;
                foreach (var group in AccountGroups)
                {
                    var accountFacts = facts.Where(x => x.LocalAccountId == group.Account.Account.Id)
                        .Select(fact => CreateFactItem(fact, group));
                    foreach (var item in Order(accountFacts)) group.Items.Add(item);
                }
                RebuildFlatItems();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                AccountGroups.Clear();
                FlatItems.Clear();
                IsEmpty = false;
                IsUnavailable = false;
                LoadError = Translator.DailyBriefing_LocalErrorMessage;
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!token.IsCancellationRequested)
                await _dispatcher.ExecuteOnUIThread(() => IsLoading = false).ConfigureAwait(false);
        }
    }

    /// <summary>Urgent and high items first; everything else keeps the local service order.</summary>
    private static IEnumerable<DailyBriefingDisplayItem> Order(IEnumerable<DailyBriefingDisplayItem> items)
    {
        var materialized = items.ToArray();
        return materialized.Where(static x => x.IsPriority)
            .Concat(materialized.Where(static x => !x.IsPriority));
    }

    private void RebuildFlatItems()
    {
        FlatItems.Clear();
        var all = AccountGroups.SelectMany(static group => group.Items).ToArray();
        foreach (var item in all.Where(static x => x.IsPriority).Concat(all.Where(static x => !x.IsPriority)))
        {
            item.IsAccountVisible = !IsGrouped;
            FlatItems.Add(item);
        }
        IsEmpty = all.Length == 0;
    }

    private DailyBriefingDisplayItem CreateFactItem(DailyBriefingFact fact, DailyBriefingAccountGroup group)
    {
        var isPriority = fact.Fact.Urgency is MailPriority.Urgent or MailPriority.High;
        var localTime = TimeZoneInfo.ConvertTime(fact.OccurredAt, _dateContext.TimeZone);
        var category = Category(fact.Fact);
        var calendarArgs = fact.Fact.PrimaryAction is AddToCalendarActionPayload add
            ? CreateCalendarArgs(fact, add.TemporalReferenceIndex)
            : null;
        var code = (fact.Fact.PrimaryAction as CopyVerificationCodeActionPayload)?.Code ?? string.Empty;
        var action = DailyBriefingActionPresentationFactory.Create(
            fact.Fact.PrimaryAction,
            canAddToCalendar: calendarArgs is not null,
            hasVerificationCode: !string.IsNullOrWhiteSpace(code));

        return new()
        {
            Headline = string.IsNullOrWhiteSpace(fact.Headline) ? fact.Subject : fact.Headline,
            Detail = StatusText(fact.Fact.Status),
            Source = BuildSource(fact.Headline, fact.Sender, fact.Subject),
            When = localTime.ToString("t", _dateContext.Culture),
            DueText = FormatTemporal(fact.Fact.TemporalReferences.FirstOrDefault()),
            CategoryText = CategoryText(category),
            CategoryGlyph = DailyBriefingIcons.Category(category),
            Tone = CategoryTone(category),
            UrgencyText = isPriority ? UrgencyText(fact.Fact.Urgency) : string.Empty,
            IsPriority = isPriority,
            MailUniqueId = fact.MailUniqueId,
            VerificationCode = code,
            CalendarArgs = calendarArgs,
            Action = action,
            AccountName = group.AccountName,
            AccountInitials = group.AccountInitials,
        };
    }

    private static string BuildSource(string headline, string sender, string subject)
    {
        var hasSubject = !string.IsNullOrWhiteSpace(subject)
            && !headline.Contains(subject, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subject, headline, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(sender)) return hasSubject ? subject : string.Empty;
        return hasSubject ? $"{sender} · {subject}" : sender;
    }

    private CalendarEventComposeNavigationArgs CreateCalendarArgs(string title, DateTimeOffset? dueAtUtc,
        DateOnly? localDate, DateOnly? localDateEnd, string subject, string sender)
    {
        var isAllDay = dueAtUtc is null && localDate is not null;
        var start = dueAtUtc is { } instant
            ? TimeZoneInfo.ConvertTime(instant, _dateContext.TimeZone).DateTime
            : localDate?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Now;
        var end = isAllDay
            ? localDateEnd?.AddDays(1).ToDateTime(TimeOnly.MinValue) ?? start.AddDays(1)
            : start.AddMinutes(30);

        return new()
        {
            Title = title,
            StartDate = start,
            EndDate = end,
            IsAllDay = isAllDay,
            NotesHtml = $"<p><strong>{WebUtility.HtmlEncode(subject)}</strong><br>{WebUtility.HtmlEncode(sender)}</p>",
        };
    }

    private CalendarEventComposeNavigationArgs? CreateCalendarArgs(DailyBriefingFact fact, int temporalReferenceIndex)
    {
        if (temporalReferenceIndex < 0 || temporalReferenceIndex >= fact.Fact.TemporalReferences.Count) return null;
        var temporal = fact.Fact.TemporalReferences[temporalReferenceIndex];
        var (start, end) = temporal switch
        {
            DeadlineTemporalPayload x => (x.Due, (TemporalPointPayload?)null),
            EventTemporalPayload x => (x.Start, x.End),
            DateRangeTemporalPayload x => (x.Start, x.End),
            AvailabilityWindowTemporalPayload x => (x.Opens, x.Closes),
            ExpectedTemporalPayload x => (x.ExpectedAt, (TemporalPointPayload?)null),
            ExpirationTemporalPayload x => (x.ExpiresAt, (TemporalPointPayload?)null),
            RenewalTemporalPayload x => (x.RenewsAt, (TemporalPointPayload?)null),
            TravelTemporalPayload x => (x.Departure, x.Arrival),
            _ => ((TemporalPointPayload?)null, null),
        };
        if (start is null) return null;
        return CreateCalendarArgs(
            string.IsNullOrWhiteSpace(fact.Subject) ? fact.Headline : fact.Subject,
            start.InstantUtc, start.LocalDate, end?.LocalDate, fact.Subject, fact.Sender);
    }

    private string FormatTemporal(TemporalPayload? temporal) => temporal switch
    {
        DeadlineTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalDue, x.Due),
        EventTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalEvent, x.Start, x.End),
        DateRangeTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalRange, x.Start, x.End),
        AvailabilityWindowTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalAvailable, x.Opens, x.Closes),
        CoveragePeriodTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalCoverage, x.Start, x.End),
        ExpectedTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalExpected, x.ExpectedAt),
        ExpirationTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalExpires, x.ExpiresAt),
        RenewalTemporalPayload x => FormatSingle(Translator.DailyBriefing_TemporalRenews, x.RenewsAt),
        TravelTemporalPayload x => FormatRange(Translator.DailyBriefing_TemporalTravel, x.Departure, x.Arrival),
        _ => string.Empty,
    };

    private string FormatSingle(string label, TemporalPointPayload point)
    {
        var value = FormatPoint(point);
        return string.IsNullOrEmpty(value) ? string.Empty : $"{label} {value}";
    }

    private string FormatRange(string label, TemporalPointPayload start, TemporalPointPayload? end)
    {
        var startText = FormatPoint(start);
        if (string.IsNullOrEmpty(startText)) return string.Empty;
        var endText = end is null ? string.Empty : FormatPoint(end);
        return string.IsNullOrEmpty(endText) ? $"{label} {startText}" : $"{label} {startText} – {endText}";
    }

    private string FormatPoint(TemporalPointPayload point)
    {
        if (point.InstantUtc is { } instant)
            return TimeZoneInfo.ConvertTime(instant, _dateContext.TimeZone).ToString("g", _dateContext.Culture);
        if (point.LocalDate is { } date && point.LocalTime is { } time)
        {
            var zone = string.IsNullOrWhiteSpace(point.TimeZoneId) ? string.Empty : $" {point.TimeZoneId}";
            return $"{date.ToString("d", _dateContext.Culture)} {time.ToString("t", _dateContext.Culture)}{zone}";
        }
        if (point.LocalDate is { } localDate) return localDate.ToString("d", _dateContext.Culture);
        return point.LocalTime?.ToString("t", _dateContext.Culture) ?? string.Empty;
    }

    [RelayCommand]
    private async Task ExecuteActionAsync(DailyBriefingDisplayItem? item)
    {
        if (item is null) return;
        switch (item.Action.Execution)
        {
            case DailyBriefingActionExecution.OpenSource:
                OpenItem(item);
                break;
            case DailyBriefingActionExecution.Reply:
                await ReplyAsync(item).ConfigureAwait(false);
                break;
            case DailyBriefingActionExecution.CopyVerificationCode:
                if (!string.IsNullOrWhiteSpace(item.VerificationCode))
                    await _clipboardService.CopyClipboardAsync(item.VerificationCode).ConfigureAwait(false);
                break;
            case DailyBriefingActionExecution.AddToCalendar:
                AddToCalendar(item);
                break;
        }
    }

    [RelayCommand]
    private void OpenItem(DailyBriefingDisplayItem? item)
    {
        if (item?.MailUniqueId is not Guid mailId) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(mailId, ScrollToItem: true));
    }

    private void AddToCalendar(DailyBriefingDisplayItem item)
    {
        if (item.CalendarArgs is not { } args) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _navigationService.ChangeApplicationMode(WinoApplicationMode.Calendar,
            new ShellModeActivationContext { Parameter = args });
    }

    private async Task ReplyAsync(DailyBriefingDisplayItem item)
    {
        if (item.MailUniqueId is not Guid mailId) return;

        try
        {
            var mailCopy = await _mailService.GetSingleMailItemAsync(mailId).ConfigureAwait(false);
            if (mailCopy?.AssignedAccount is null || mailCopy.FileId == Guid.Empty) return;

            var mimeInformation = await _mimeFileService
                .GetMimeMessageInformationAsync(mailCopy.FileId, mailCopy.AssignedAccount.Id)
                .ConfigureAwait(false);
            if (mimeInformation?.MimeMessage is null) return;

            var options = new DraftCreationOptions
            {
                Reason = DraftCreationReason.Reply,
                ReferencedMessage = new ReferencedMessage { MimeMessage = mimeInformation.MimeMessage, MailCopy = mailCopy },
            };
            var (draftMailCopy, draftBase64MimeMessage) = await _mailService
                .CreateDraftAsync(mailCopy.AssignedAccount.Id, options).ConfigureAwait(false);
            await _dispatcher.ExecuteOnUIThread(() => CloseRequested?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
            await _requestDelegator.ExecuteAsync(new DraftPreparationRequest(mailCopy.AssignedAccount,
                draftMailCopy, draftBase64MimeMessage, options.Reason, mailCopy)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.ExecuteOnUIThread(() =>
                _dialogService.InfoBarMessage(Translator.Info_DraftCreationFailed, exception.Message,
                    InfoBarMessageType.Error)).ConfigureAwait(false);
        }
    }

    public Task MarkViewedAsync() => _localService.MarkViewedAsync();

    internal static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? parts[0][..Math.Min(2, parts[0].Length)].ToUpper(CultureInfo.CurrentCulture)
            : $"{parts[0][0]}{parts[^1][0]}".ToUpper(CultureInfo.CurrentCulture);
    }

    private static string UrgencyText(MailPriority priority) => priority switch
    {
        MailPriority.Urgent => Translator.DailyBriefing_UrgencyUrgent,
        _ => Translator.DailyBriefing_UrgencyHigh,
    };

    private static BriefingFactCategory Category(BriefingFactCapabilityPayload fact) => fact switch
    {
        SecurityFactPayload or AccountFactPayload => BriefingFactCategory.Security,
        FinanceFactPayload or PurchaseFactPayload or SubscriptionFactPayload => BriefingFactCategory.Finance,
        TravelFactPayload or ReservationFactPayload => BriefingFactCategory.Travel,
        ConversationFactPayload or SocialFactPayload => BriefingFactCategory.Personal,
        TaskFactPayload or ApprovalFactPayload or MeetingFactPayload => BriefingFactCategory.ActionRequired,
        _ when fact.Status is BriefingStatus.AwaitingMyReply or BriefingStatus.AwaitingOthers => BriefingFactCategory.Waiting,
        _ => BriefingFactCategory.Information,
    };

    private static DailyBriefingTone CategoryTone(BriefingFactCategory category) => category switch
    {
        BriefingFactCategory.ActionRequired or BriefingFactCategory.Security => DailyBriefingTone.Critical,
        BriefingFactCategory.Finance or BriefingFactCategory.Waiting => DailyBriefingTone.Caution,
        BriefingFactCategory.Travel => DailyBriefingTone.Success,
        BriefingFactCategory.Personal => DailyBriefingTone.Attention,
        _ => DailyBriefingTone.Neutral,
    };

    private static string CategoryText(BriefingFactCategory category) => category switch
    {
        BriefingFactCategory.Information => Translator.IntelligenceTile_BriefingCategoryInformation,
        BriefingFactCategory.ActionRequired => Translator.IntelligenceTile_BriefingCategoryActionRequired,
        BriefingFactCategory.Waiting => Translator.IntelligenceTile_BriefingCategoryWaiting,
        BriefingFactCategory.Security => Translator.IntelligenceTile_BriefingCategorySecurity,
        BriefingFactCategory.Finance => Translator.IntelligenceTile_BriefingCategoryFinance,
        BriefingFactCategory.Travel => Translator.IntelligenceTile_BriefingCategoryTravel,
        BriefingFactCategory.Personal => Translator.IntelligenceTile_BriefingCategoryPersonal,
        _ => Translator.IntelligenceTile_BriefingCategoryOther,
    };

    private static string StatusText(BriefingStatus status) => status switch
    {
        BriefingStatus.ActionRequired => Translator.DailyBriefing_StatusActionRequired,
        BriefingStatus.AwaitingMyReply => Translator.DailyBriefing_StatusAwaitingMyReply,
        BriefingStatus.AwaitingOthers => Translator.DailyBriefing_StatusAwaitingOthers,
        BriefingStatus.Scheduled => Translator.DailyBriefing_StatusScheduled,
        BriefingStatus.InProgress => Translator.DailyBriefing_StatusInProgress,
        BriefingStatus.Completed => Translator.DailyBriefing_StatusCompleted,
        BriefingStatus.Updated => Translator.DailyBriefing_StatusUpdated,
        BriefingStatus.Cancelled => Translator.DailyBriefing_StatusCancelled,
        BriefingStatus.Expired => Translator.DailyBriefing_StatusExpired,
        _ => string.Empty,
    };

    public void Dispose() => CancelPendingWork();
}
