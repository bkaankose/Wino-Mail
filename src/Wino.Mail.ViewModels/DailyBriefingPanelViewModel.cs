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
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Drives the daily briefing panel.
/// The briefing is a flat, reverse-chronological list of the messages Jev included,
/// grouped by the day each one was received. There is no date picker, no upcoming
/// window and no per-card action: Jev cannot produce dates or actions, so the panel
/// never pretends it can.
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
        IMailDialogService dialogService)
    {
        _localService = localService;
        _dateContext = dateContext;
        _dispatcher = dispatcher;
        _preferencesService = preferencesService;
        _dialogService = dialogService;
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
