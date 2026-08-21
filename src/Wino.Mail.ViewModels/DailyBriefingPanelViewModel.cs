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
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public sealed partial class DailyBriefingPanelViewModel : ObservableObject, IRecipient<IntelligenceVisibilityChanged>, IDisposable
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
    private DailyBriefingDateItem? _selectedDate;

    public ObservableCollection<DailyBriefingDateItem> Dates { get; } = [];

    [ObservableProperty]
    public partial int SelectedDateIndex { get; set; }

    public DailyBriefingDateItem? SelectedDate
    {
        get => _selectedDate;
        private set
        {
            if (ReferenceEquals(_selectedDate, value)) return;

            _selectedDate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDateGroups));
            UpdateEmptyState();
        }
    }

    public ObservableCollection<DailyBriefingAccountGroup>? SelectedDateGroups => SelectedDate?.Groups;

    [ObservableProperty]
    public partial bool IsShowingIgnored { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsFilteredEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string LoadError { get; set; } = string.Empty;

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);
    public bool ShowContent => !IsLoading && !IsUnavailable && !IsEmpty && !IsFilteredEmpty && !HasLoadError;
    public bool ShowFilteredEmpty => !IsLoading && !IsUnavailable && IsFilteredEmpty && !HasLoadError;

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
        IsShowingIgnored = preferencesService.IsDailyBriefingShowingIgnored;
        WeakReferenceMessenger.Default.Register(this);
    }

    public event EventHandler? CloseRequested;

    public async Task InitializeAsync()
    {
        if (Dates.Count == 0)
            BuildDates();

        await _dispatcher.ExecuteOnUIThread(() =>
        {
            _isInitialized = false;
            SelectedDateIndex = 0;
            SelectedDate = Dates[0];
            _isInitialized = true;
        }).ConfigureAwait(false);

        await LoadAllDatesAsync(refreshAccounts: true).ConfigureAwait(false);
    }

    private void BuildDates()
    {
        var today = _dateContext.GetToday();
        for (var offset = 0; offset < DateCount; offset++)
        {
            var date = today.AddDays(-offset);
            var moment = date.ToDateTime(TimeOnly.MinValue);
            Dates.Add(new DailyBriefingDateItem
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
        if (!_isInitialized || value < 0 || value >= Dates.Count) return;

        SelectedDate = Dates[value];
    }

    partial void OnIsShowingIgnoredChanged(bool value)
    {
        _preferencesService.IsDailyBriefingShowingIgnored = value;
        if (!_isInitialized) return;

        foreach (var date in Dates)
            ApplyProjection(date);

        UpdateEmptyState();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
    }

    partial void OnIsUnavailableChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
    }

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowContent));

    partial void OnIsFilteredEmptyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
    }

    partial void OnLoadErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAllDatesAsync(refreshAccounts: true);

    private void CancelPendingWork()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private async Task LoadAllDatesAsync(bool refreshAccounts)
    {
        CancelPendingWork();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;

        await _dispatcher.ExecuteOnUIThread(() =>
        {
            IsLoading = true;
            IsEmpty = false;
            IsFilteredEmpty = false;
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

            if (_eligibleAccounts.Count == 0)
            {
                await _dispatcher.ExecuteOnUIThread(() =>
                {
                    foreach (var date in Dates)
                    {
                        date.Facts.Clear();
                        ClearGroups(date.Groups);
                        date.HasIgnoredFacts = false;
                    }

                    IsUnavailable = true;
                    UpdateEmptyState();
                }).ConfigureAwait(false);
                return;
            }

            var results = await Task.WhenAll(Dates.Select(date =>
                _localService.GetBriefingFactsAsync(date.Date, _dateContext.TimeZone,
                    includeIgnored: true, cancellationToken: token))).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (token.IsCancellationRequested) return;

                for (var index = 0; index < Dates.Count; index++)
                {
                    var date = Dates[index];
                    date.Facts.Clear();
                    date.Facts.AddRange(results[index].Facts);
                    date.HasIgnoredFacts = results[index].HasIgnoredFacts || date.Facts.Any(static fact => fact.IsIgnored);
                    ApplyProjection(date);
                }

                UpdateEmptyState();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var date in Dates)
                {
                    date.Facts.Clear();
                    ClearGroups(date.Groups);
                    date.HasIgnoredFacts = false;
                }

                IsUnavailable = false;
                IsEmpty = false;
                IsFilteredEmpty = false;
                LoadError = Translator.DailyBriefing_LocalErrorMessage;
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!token.IsCancellationRequested)
                await _dispatcher.ExecuteOnUIThread(() => IsLoading = false).ConfigureAwait(false);
        }
    }

    private void ApplyProjection(DailyBriefingDateItem date)
    {
        var desiredGroups = new List<(DailyBriefingAccount Account, IReadOnlyList<DailyBriefingFact> Facts)>();
        foreach (var account in _eligibleAccounts)
        {
            var facts = date.Facts
                .Where(fact => fact.LocalAccountId == account.Account.Id && (IsShowingIgnored || !fact.IsIgnored))
                .OrderByDescending(static fact => fact.IsPriorityVisible && fact.Fact.Urgency is MailPriority.Urgent or MailPriority.High)
                .ThenByDescending(static fact => fact.OccurredAt)
                .ToArray();
            if (facts.Length > 0)
                desiredGroups.Add((account, facts));
        }

        var existingItems = date.Groups.SelectMany(static group => group).ToDictionary(GetItemKey);
        for (var groupIndex = 0; groupIndex < desiredGroups.Count; groupIndex++)
        {
            var desired = desiredGroups[groupIndex];
            var group = date.Groups.FirstOrDefault(candidate => candidate.Account.Account.Id == desired.Account.Account.Id);
            if (group is null)
            {
                group = new DailyBriefingAccountGroup(desired.Account, []);
                date.Groups.Insert(groupIndex, group);
            }
            else if (date.Groups.IndexOf(group) != groupIndex)
            {
                date.Groups.Move(date.Groups.IndexOf(group), groupIndex);
            }

            UpdateGroupItems(group, desired.Facts, desired.Account, existingItems);
        }

        while (date.Groups.Count > desiredGroups.Count)
        {
            var group = date.Groups[^1];
            while (group.Count > 0)
                group.RemoveAt(group.Count - 1);
            date.Groups.RemoveAt(date.Groups.Count - 1);
        }
    }

    private void UpdateGroupItems(DailyBriefingAccountGroup group, IReadOnlyList<DailyBriefingFact> facts,
        DailyBriefingAccount account, IReadOnlyDictionary<DailyBriefingItemKey, DailyBriefingItem> existingItems)
    {
        for (var targetIndex = 0; targetIndex < facts.Count; targetIndex++)
        {
            var fact = facts[targetIndex];
            var key = GetItemKey(fact);
            DailyBriefingItem item;
            if (existingItems.TryGetValue(key, out var existing) && existing.ArtifactRevision == fact.ArtifactRevision)
            {
                item = existing;
                item.IsIgnored = fact.IsIgnored;
            }
            else
            {
                item = CreateItem(fact, account);
            }

            if (targetIndex < group.Count && ReferenceEquals(group[targetIndex], item))
                continue;

            var existingIndex = group.IndexOf(item);
            if (existingIndex >= 0)
                group.Move(existingIndex, targetIndex);
            else
                group.Insert(targetIndex, item);
        }

        while (group.Count > facts.Count)
            group.RemoveAt(group.Count - 1);
    }

    private DailyBriefingItem CreateItem(DailyBriefingFact fact, DailyBriefingAccount account)
    {
        var calendarArgs = fact.Fact.PrimaryAction is AddToCalendarActionPayload add
            ? CreateCalendarArgs(fact, add.TemporalReferenceIndex)
            : null;
        var code = (fact.Fact.PrimaryAction as CopyVerificationCodeActionPayload)?.Code ?? string.Empty;
        var item = new DailyBriefingItem(fact, account, calendarArgs, code);
        item.IsIgnored = fact.IsIgnored;
        return item;
    }

    private static DailyBriefingItemKey GetItemKey(DailyBriefingFact fact)
        => fact.Fact.BriefingId != Guid.Empty
            ? new(fact.LocalAccountId, fact.Fact.BriefingId, true)
            : new(fact.LocalAccountId, fact.MailUniqueId, false);

    private static DailyBriefingItemKey GetItemKey(DailyBriefingItem item) => GetItemKey(item.Fact);

    private static void ClearGroups(ObservableCollection<DailyBriefingAccountGroup> groups)
    {
        while (groups.Count > 0)
            groups.RemoveAt(groups.Count - 1);
    }

    private readonly record struct DailyBriefingItemKey(Guid LocalAccountId, Guid ItemId, bool IsBriefingId);

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

    [RelayCommand]
    private async Task ExecuteActionAsync(DailyBriefingItem? item)
    {
        if (item is null) return;

        var action = DailyBriefingActionPresentationFactory.Create(
            item.Fact.Fact.PrimaryAction,
            canAddToCalendar: item.CalendarArgs is not null,
            hasVerificationCode: !string.IsNullOrWhiteSpace(item.VerificationCode),
            allowReplyAction: item.IndicatorState.IsNeedsReplyVisible);
        switch (action.Execution)
        {
            case DailyBriefingActionExecution.OpenSource:
                OpenItem(item);
                break;
            case DailyBriefingActionExecution.Reply:
                await ReplyAsync(item).ConfigureAwait(false);
                break;
            case DailyBriefingActionExecution.CopyVerificationCode:
                await _clipboardService.CopyClipboardAsync(item.VerificationCode).ConfigureAwait(false);
                break;
            case DailyBriefingActionExecution.AddToCalendar:
                AddToCalendar(item);
                break;
        }
    }

    [RelayCommand]
    private void OpenItem(DailyBriefingItem? item)
    {
        if (item is null || item.MailUniqueId == Guid.Empty) return;

        CloseRequested?.Invoke(this, EventArgs.Empty);
        WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(item.MailUniqueId, ScrollToItem: true));
    }

    private void AddToCalendar(DailyBriefingItem item)
    {
        if (item.CalendarArgs is not { } args) return;

        CloseRequested?.Invoke(this, EventArgs.Empty);
        _navigationService.ChangeApplicationMode(WinoApplicationMode.Calendar,
            new ShellModeActivationContext { Parameter = args });
    }

    private async Task ReplyAsync(DailyBriefingItem item)
    {
        if (item.MailUniqueId == Guid.Empty) return;

        try
        {
            var mailCopy = await _mailService.GetSingleMailItemAsync(item.MailUniqueId).ConfigureAwait(false);
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

    [RelayCommand]
    private async Task IgnoreAsync(DailyBriefingItem? item)
    {
        if (item is null || !item.CanToggleIgnore) return;

        var wasIgnored = item.IsIgnored;
        await _dispatcher.ExecuteOnUIThread(() => item.IsIgnorePending = true).ConfigureAwait(false);
        try
        {
            if (wasIgnored)
                await _localService.UnignoreBriefingItemAsync(item.LocalAccountId, item.BriefingId).ConfigureAwait(false);
            else
                await _localService.IgnoreBriefingItemAsync(item.LocalAccountId, item.BriefingId, item.ArtifactRevision)
                    .ConfigureAwait(false);

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var date in Dates)
                {
                    for (var index = 0; index < date.Facts.Count; index++)
                    {
                        var fact = date.Facts[index];
                        if (fact.LocalAccountId == item.LocalAccountId && fact.Fact.BriefingId == item.BriefingId)
                            date.Facts[index] = fact with { IsIgnored = !wasIgnored };
                    }

                    date.HasIgnoredFacts = date.Facts.Any(static fact => fact.IsIgnored);
                    ApplyProjection(date);
                }

                item.IsIgnorePending = false;
                UpdateEmptyState();
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                item.IsIgnorePending = false;
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                    $"{Translator.DailyBriefing_LocalActionError} {exception.Message}", InfoBarMessageType.Error);
            }).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(DailyBriefingItem? item)
    {
        if (item is null || !item.CanDelete) return;

        await _dispatcher.ExecuteOnUIThread(() => item.IsDeletePending = true).ConfigureAwait(false);
        try
        {
            await _localService.DeleteBriefingItemAsync(item.LocalAccountId, item.Fact.RemoteMessageId)
                .ConfigureAwait(false);

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var date in Dates)
                {
                    date.Facts.RemoveAll(fact => fact.LocalAccountId == item.LocalAccountId &&
                        fact.Fact.BriefingId == item.BriefingId);
                    date.HasIgnoredFacts = date.Facts.Any(static fact => fact.IsIgnored);
                    ApplyProjection(date);
                }

                item.IsDeletePending = false;
                UpdateEmptyState();
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                item.IsDeletePending = false;
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                    $"{Translator.DailyBriefing_LocalActionError} {exception.Message}", InfoBarMessageType.Error);
            }).ConfigureAwait(false);
        }
    }

    private void UpdateEmptyState()
    {
        var hasItems = SelectedDate?.Groups.Any(static group => group.Count > 0) == true;
        var hasIgnoredFacts = SelectedDate?.HasIgnoredFacts == true;
        IsFilteredEmpty = !IsShowingIgnored && hasIgnoredFacts && !hasItems;
        IsEmpty = !hasItems && !IsFilteredEmpty;
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

    public void Receive(IntelligenceVisibilityChanged message)
    {
        if (_isInitialized)
            _ = LoadAllDatesAsync(refreshAccounts: true);
    }

    public void Dispose()
    {
        CancelPendingWork();
        WeakReferenceMessenger.Default.Unregister<IntelligenceVisibilityChanged>(this);
    }
}
