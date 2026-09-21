#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
using Wino.Core.Domain.Models.Requests;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Drives the daily briefing panel.
/// The briefing is a flat, reverse-chronological list of the messages Classification included,
/// grouped by the day each one was received. There is no date picker and no upcoming window,
/// because Classification produces no dates. It does produce one action per message, and the card's
/// command follows it.
/// </summary>
public sealed partial class DailyBriefingPanelViewModel : ObservableObject,
    IRecipient<IntelligenceVisibilityChanged>,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IDisposable
{
    private readonly ILocalIntelligenceService _localService;
    private readonly IDateContextProvider _dateContext;
    private readonly IDispatcher _dispatcher;
    private readonly IPreferencesService _preferencesService;
    private readonly IMailDialogService _dialogService;
    private readonly IMailService _mailService;
    private readonly IMimeFileService _mimeFileService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly IClipboardService _clipboardService;

    private CancellationTokenSource? _loadCancellation;
    private IReadOnlyList<DailyBriefingAccount> _eligibleAccounts = [];
    private bool _isInitialized;
    private DateTime? _lastViewedUtc;

    /// <summary>Day groups, newest first.</summary>
    public ObservableCollection<DailyBriefingDateItem> Days { get; } = [];

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

    [ObservableProperty]
    public partial int NewItemCount { get; set; }

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public bool ShowContent => !IsLoading && !IsUnavailable && !IsEmpty && !IsFilteredEmpty && !HasLoadError;

    public bool ShowFilteredEmpty => !IsLoading && !IsUnavailable && IsFilteredEmpty && !HasLoadError;

    public DailyBriefingPanelViewModel(
        ILocalIntelligenceService localService,
        IDateContextProvider dateContext,
        IDispatcher dispatcher,
        IPreferencesService preferencesService,
        IMailDialogService dialogService,
        IMailService mailService,
        IMimeFileService mimeFileService,
        IWinoRequestDelegator requestDelegator,
        IClipboardService clipboardService)
    {
        _localService = localService;
        _dateContext = dateContext;
        _dispatcher = dispatcher;
        _preferencesService = preferencesService;
        _dialogService = dialogService;
        _mailService = mailService;
        _mimeFileService = mimeFileService;
        _requestDelegator = requestDelegator;
        _clipboardService = clipboardService;
        IsShowingIgnored = preferencesService.IsDailyBriefingShowingIgnored;
        WeakReferenceMessenger.Default.Register<IntelligenceVisibilityChanged>(this);
        WeakReferenceMessenger.Default.Register<IntelligenceMetadataChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceEntitlementChanged>(this);
    }

    public event EventHandler? CloseRequested;

    public async Task InitializeAsync()
    {
        _isInitialized = true;
        await _localService.MarkOpenedAsync().ConfigureAwait(false);
        await LoadAsync(refreshAccounts: true).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync(refreshAccounts: true);

    partial void OnIsShowingIgnoredChanged(bool value)
    {
        _preferencesService.IsDailyBriefingShowingIgnored = value;
        if (_isInitialized)
        {
            _ = LoadAsync(refreshAccounts: false);
        }
    }

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
            IsLoading = true;
            LoadError = string.Empty;
        }).ConfigureAwait(false);

        try
        {
            if (refreshAccounts || _eligibleAccounts.Count == 0)
            {
                _eligibleAccounts = await _localService.GetEligibleAccountsAsync(token).ConfigureAwait(false);
            }

            if (_eligibleAccounts.Count == 0)
            {
                await _dispatcher.ExecuteOnUIThread(() =>
                {
                    Days.Clear();
                    IsUnavailable = true;
                    IsLoading = false;
                    UpdateEmptyState();
                }).ConfigureAwait(false);
                return;
            }

            var unseen = await _localService.GetUnseenStateAsync(token).ConfigureAwait(false);
            _lastViewedUtc = unseen.LastViewedUtc;

            var result = await _localService
                .GetBriefingFactsAsync(_dateContext.TimeZone, IsShowingIgnored, token)
                .ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                return;
            }

            var accountsById = _eligibleAccounts.ToDictionary(static account => account.Account.Id);

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                Days.Clear();
                var newCount = 0;

                foreach (var day in result.Days)
                {
                    var items = new List<DailyBriefingItem>(day.Facts.Count);
                    foreach (var fact in day.Facts)
                    {
                        if (!accountsById.TryGetValue(fact.LocalAccountId, out var account))
                        {
                            continue;
                        }

                        var item = new DailyBriefingItem(fact, account)
                        {
                            IsNew = fact.IsNewSince(_lastViewedUtc),
                        };

                        if (item.IsNew)
                        {
                            newCount++;
                        }

                        items.Add(item);
                    }

                    if (items.Count == 0)
                    {
                        continue;
                    }

                    Days.Add(new DailyBriefingDateItem
                    {
                        Date = day.LocalDate,
                        DisplayName = FormatDay(day.LocalDate),
                        SecondaryName = day.LocalDate.ToString("d", CultureInfo.CurrentCulture),
                        Items = new ObservableCollection<DailyBriefingItem>(items),
                    });
                }

                NewItemCount = newCount;
                HasIgnoredItems = result.IgnoredCount > 0;
                IsUnavailable = false;
                IsLoading = false;
                UpdateEmptyState();

                // The grouped view is rebuilt from the repopulated collection rather than left to
                // track it: a grouped CollectionViewSource that was bound while the collection was
                // still empty does not pick up the groups added afterwards.
                OnPropertyChanged(nameof(Days));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer load replaced this one.
        }
        catch (Exception exception)
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                IsLoading = false;
                LoadError = exception.Message;
                OnPropertyChanged(nameof(HasLoadError));
                OnPropertyChanged(nameof(ShowContent));
            }).ConfigureAwait(false);
        }
    }

    /// <summary>True when the current result had ignored cards that the filter hid.</summary>
    [ObservableProperty]
    public partial bool HasIgnoredItems { get; set; }

    private string FormatDay(DateOnly date)
    {
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _dateContext.TimeZone).DateTime);

        if (date == today)
        {
            return Translator.DailyBriefing_Today;
        }

        return date == today.AddDays(-1)
            ? Translator.DailyBriefing_Yesterday
            : date.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Runs the card's own command. Reply and Copy code are completed here; every other action
    /// names what the message asks for and opens it, because nothing local can complete them.
    /// </summary>
    [RelayCommand]
    private async Task ExecutePrimaryActionAsync(DailyBriefingItem? item)
    {
        if (item is null)
        {
            return;
        }

        switch (item.PrimaryAction.Execution)
        {
            case DailyBriefingActionExecution.Reply:
                await ReplyAsync(item).ConfigureAwait(false);
                break;
            case DailyBriefingActionExecution.CopyVerificationCode:
                await CopyVerificationCodeAsync(item).ConfigureAwait(false);
                break;
            default:
                await _dispatcher.ExecuteOnUIThread(() => OpenItem(item)).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Starts a reply draft for the card's message, the same way the message list does.
    /// </summary>
    private async Task ReplyAsync(DailyBriefingItem item)
    {
        if (item.MailUniqueId == Guid.Empty)
        {
            return;
        }

        try
        {
            var mailCopy = await _mailService.GetSingleMailItemAsync(item.MailUniqueId).ConfigureAwait(false);
            if (mailCopy?.AssignedAccount is null || mailCopy.FileId == Guid.Empty)
            {
                await _dispatcher.ExecuteOnUIThread(() => OpenItem(item)).ConfigureAwait(false);
                return;
            }

            var mimeInformation = await _mimeFileService
                .GetMimeMessageInformationAsync(mailCopy.FileId, mailCopy.AssignedAccount.Id)
                .ConfigureAwait(false);
            if (mimeInformation?.MimeMessage is null)
            {
                await _dispatcher.ExecuteOnUIThread(() => OpenItem(item)).ConfigureAwait(false);
                return;
            }

            var options = new DraftCreationOptions
            {
                Reason = DraftCreationReason.Reply,
                ReferencedMessage = new ReferencedMessage
                {
                    MimeMessage = mimeInformation.MimeMessage,
                    MailCopy = mailCopy,
                },
            };

            var (draftMailCopy, draftBase64MimeMessage) = await _mailService
                .CreateDraftAsync(mailCopy.AssignedAccount.Id, options)
                .ConfigureAwait(false);

            await _dispatcher.ExecuteOnUIThread(() => CloseRequested?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
            await _requestDelegator.ExecuteAsync(new DraftPreparationRequest(
                mailCopy.AssignedAccount, draftMailCopy, draftBase64MimeMessage, options.Reason, mailCopy))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.ExecuteOnUIThread(() => _dialogService.InfoBarMessage(
                Translator.Info_DraftCreationFailed, exception.Message, InfoBarMessageType.Error))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Classification reports that a message carries a one-time code but not the code itself, so the
    /// body is read here. A message whose code cannot be found opens instead of failing silently.
    /// </summary>
    private async Task CopyVerificationCodeAsync(DailyBriefingItem item)
    {
        if (item.MailUniqueId == Guid.Empty)
        {
            return;
        }

        try
        {
            var mailCopy = await _mailService.GetSingleMailItemAsync(item.MailUniqueId).ConfigureAwait(false);
            var code = mailCopy is { FileId: var fileId } && fileId != Guid.Empty && mailCopy.AssignedAccount is not null
                ? ExtractCode(await _mimeFileService
                    .GetMimeMessageInformationAsync(fileId, mailCopy.AssignedAccount.Id)
                    .ConfigureAwait(false))
                : null;

            if (string.IsNullOrEmpty(code))
            {
                await _dispatcher.ExecuteOnUIThread(() =>
                {
                    _dialogService.InfoBarMessage(
                        Translator.DailyBriefing_Title,
                        Translator.DailyBriefing_CodeNotFound,
                        InfoBarMessageType.Information);
                    OpenItem(item);
                }).ConfigureAwait(false);
                return;
            }

            await _clipboardService.CopyClipboardAsync(code).ConfigureAwait(false);
            await _dispatcher.ExecuteOnUIThread(() => _dialogService.InfoBarMessage(
                Translator.DailyBriefing_Title,
                Translator.DailyBriefing_CodeCopied,
                InfoBarMessageType.Success)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.ExecuteOnUIThread(() => _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                $"{Translator.DailyBriefing_LocalActionError} {exception.Message}",
                InfoBarMessageType.Error)).ConfigureAwait(false);
        }
    }

    private static string? ExtractCode(MimeMessageInformation? mimeInformation)
    {
        var message = mimeInformation?.MimeMessage;
        if (message is null)
        {
            return null;
        }

        return VerificationCodeExtractor.TryExtract(message.TextBody)
            ?? VerificationCodeExtractor.TryExtract(VerificationCodeExtractor.ToPlainText(message.HtmlBody))
            ?? VerificationCodeExtractor.TryExtract(message.Subject);
    }

    [RelayCommand]
    private void OpenItem(DailyBriefingItem? item)
    {
        if (item is null || item.MailUniqueId == Guid.Empty)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(item.MailUniqueId, ScrollToItem: true));
    }

    [RelayCommand]
    private async Task IgnoreAsync(DailyBriefingItem? item)
    {
        if (item is null || !item.CanToggleIgnore)
        {
            return;
        }

        var wasIgnored = item.IsIgnored;
        await _dispatcher.ExecuteOnUIThread(() => item.IsIgnorePending = true).ConfigureAwait(false);
        try
        {
            if (wasIgnored)
            {
                await _localService
                    .UnignoreBriefingItemAsync(item.LocalAccountId, item.RemoteMessageId)
                    .ConfigureAwait(false);
            }
            else
            {
                // Ignoring is keyed on the content hash, so a message whose content later
                // changes comes back rather than staying hidden forever.
                await _localService
                    .IgnoreBriefingItemAsync(item.LocalAccountId, item.RemoteMessageId, item.ContentHash)
                    .ConfigureAwait(false);
            }

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                item.IsIgnored = !wasIgnored;
                item.IsIgnorePending = false;

                if (item.IsIgnored && !IsShowingIgnored)
                {
                    RemoveItem(item);
                }

                UpdateEmptyState();
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                item.IsIgnorePending = false;
                _dialogService.InfoBarMessage(
                    Translator.GeneralTitle_Error,
                    $"{Translator.DailyBriefing_LocalActionError} {exception.Message}",
                    InfoBarMessageType.Error);
            }).ConfigureAwait(false);
        }
    }

    private void RemoveItem(DailyBriefingItem item)
    {
        foreach (var day in Days.ToArray())
        {
            if (day.Items.Remove(item) && day.Items.Count == 0)
            {
                Days.Remove(day);
            }
        }

        HasIgnoredItems = true;
    }

    private void UpdateEmptyState()
    {
        var hasItems = Days.Any(static day => day.Items.Count > 0);
        IsFilteredEmpty = !IsShowingIgnored && HasIgnoredItems && !hasItems;
        IsEmpty = !hasItems && !IsFilteredEmpty;
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
    }

    public async Task MarkViewedAsync()
    {
        await _localService.MarkViewedAsync().ConfigureAwait(false);
        _lastViewedUtc = DateTime.UtcNow;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            NewItemCount = 0;
            foreach (var day in Days)
            {
                foreach (var item in day.Items)
                {
                    item.IsNew = false;
                }
            }
        }).ConfigureAwait(false);

        // The title-bar badge is the same unseen state, so it has to be told the briefing was read.
        WeakReferenceMessenger.Default.Send(new DailyBriefingStateChanged());
    }

    internal static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? parts[0][..Math.Min(2, parts[0].Length)].ToUpper(CultureInfo.CurrentCulture)
            : $"{parts[0][0]}{parts[^1][0]}".ToUpper(CultureInfo.CurrentCulture);
    }

    public void Receive(IntelligenceVisibilityChanged message)
    {
        if (_isInitialized)
        {
            _ = LoadAsync(refreshAccounts: true);
        }
    }

    /// <summary>Newly imported artifacts change what the briefing should show.</summary>
    public void Receive(IntelligenceMetadataChanged message)
    {
        if (_isInitialized)
        {
            _ = LoadAsync(refreshAccounts: false);
        }
    }

    public void Receive(WinoIntelligenceEntitlementChanged message)
    {
        if (message.Entitlement.CanAccessSurfaces)
        {
            return;
        }

        CancelPendingWork();
        _eligibleAccounts = [];
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            Days.Clear();
            IsLoading = false;
            IsUnavailable = true;
            LoadError = string.Empty;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Dispose()
    {
        CancelPendingWork();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
