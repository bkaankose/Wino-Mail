#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

public partial class WinoAccountConsentPageViewModel : CoreBaseViewModel
{
    private readonly IWinoAccountApiClient _apiClient;
    private readonly IAccountService _accountService;
    private readonly ISemanticIndexCoordinator _semanticIndexCoordinator;
    private string _transportPolicyVersion = string.Empty;
    private string _processPolicyVersion = string.Empty;

    public WinoAccountConsentPageViewModel(
        IWinoAccountApiClient apiClient,
        IAccountService accountService,
        ISemanticIndexCoordinator semanticIndexCoordinator)
    {
        _apiClient = apiClient;
        _accountService = accountService;
        _semanticIndexCoordinator = semanticIndexCoordinator;
    }

    public ObservableCollection<WinoConsentMailboxItemViewModel> Mailboxes { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsTransportBusy { get; set; }

    [ObservableProperty]
    public partial bool IsBulkBusy { get; set; }

    [ObservableProperty]
    public partial bool IsTransportConsentGranted { get; set; }

    [ObservableProperty]
    public partial Uri? TransportPolicyUri { get; set; }

    [ObservableProperty]
    public partial Uri? ProcessPolicyUri { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string TransportPolicyVersion => _transportPolicyVersion;
    public string ProcessPolicyVersion => _processPolicyVersion;

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        await ExecuteUIThread(() =>
        {
            IsLoading = true;
            ErrorMessage = string.Empty;
            OnPropertyChanged(nameof(HasError));
        });
        try
        {
            var transportTask = _apiClient.GetTransportConsentAsync();
            var processTask = _apiClient.GetProcessConsentsAsync();
            var localTask = _accountService.GetAccountsAsync();
            await Task.WhenAll(transportTask, processTask, localTask).ConfigureAwait(false);

            var transport = await transportTask.ConfigureAwait(false);
            var processList = await processTask.ConfigureAwait(false);
            var processConsents = processList.Mailboxes;
            var localAccounts = await localTask.ConfigureAwait(false) ?? [];
            var processByKey = processConsents.ToDictionary(
                x => CreateKey(x.Address, (MailProviderType)x.ProviderType),
                StringComparer.Ordinal);
            var localByKey = localAccounts.ToDictionary(
                x => CreateKey(x.Address, x.ProviderType),
                StringComparer.Ordinal);
            var keys = processByKey.Keys.Concat(localByKey.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();
            var rows = new List<WinoConsentMailboxItemViewModel>(keys.Length);
            foreach (var key in keys)
            {
                processByKey.TryGetValue(key, out var process);
                localByKey.TryGetValue(key, out var local);
                rows.Add(new WinoConsentMailboxItemViewModel
                {
                    Address = process?.Address ?? local!.Address,
                    ProviderType = process is null ? local!.ProviderType : (MailProviderType)process.ProviderType,
                    LocalAccountId = local?.Id,
                    MailboxId = process?.MailboxId,
                    IsProcessConsentGranted = IsCurrent(process),
                });
            }

            await ExecuteUIThread(() =>
            {
                _transportPolicyVersion = transport.CurrentPolicyVersion;
                _processPolicyVersion = processList.CurrentPolicyVersion;
                TransportPolicyUri = CreateUri(transport.PrivacyPolicyUrl);
                ProcessPolicyUri = CreateUri(processList.PrivacyPolicyUrl);
                IsTransportConsentGranted = IsCurrent(transport);
                Mailboxes.Clear();
                foreach (var row in rows)
                    Mailboxes.Add(row);
            });
        }
        catch (Exception exception)
        {
            await SetErrorAsync(exception.Message);
        }
        finally
        {
            await ExecuteUIThread(() => IsLoading = false);
        }
    }

    public async Task<bool> SetTransportConsentAsync(bool granted)
    {
        await ExecuteUIThread(() => IsTransportBusy = true);
        try
        {
            var consent = granted
                ? await _apiClient.AcceptTransportConsentAsync(_transportPolicyVersion, ConsentActionSources.ConsentPage).ConfigureAwait(false)
                : await _apiClient.RevokeTransportConsentAsync(ConsentActionSources.ConsentPage).ConfigureAwait(false);
            var current = IsCurrent(consent);
            await ExecuteUIThread(() => IsTransportConsentGranted = current);
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
            return current;
        }
        catch (Exception exception)
        {
            await SetErrorAsync(exception.Message);
            return IsTransportConsentGranted;
        }
        finally
        {
            await ExecuteUIThread(() => IsTransportBusy = false);
        }
    }

    public async Task<bool> SetProcessConsentAsync(WinoConsentMailboxItemViewModel item, bool granted, string source = ConsentActionSources.ConsentPage)
    {
        await ExecuteUIThread(() =>
        {
            item.IsBusy = true;
            item.ErrorMessage = string.Empty;
        });
        try
        {
            var mailboxId = item.MailboxId;
            if (granted && mailboxId is null)
            {
                var mailbox = await _apiClient.EnsureSemanticMailboxAsync(item.Address, (int)item.ProviderType).ConfigureAwait(false);
                mailboxId = mailbox.MailboxId;
                await ExecuteUIThread(() => item.MailboxId = mailboxId);
            }
            if (mailboxId is null)
                return false;

            if (granted)
            {
                var consent = await _apiClient.AcceptProcessConsentAsync(mailboxId.Value, _processPolicyVersion, source).ConfigureAwait(false);
                var current = IsCurrent(consent);
                await ExecuteUIThread(() => item.IsProcessConsentGranted = current);
                WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
                return current;
            }

            await _apiClient.RevokeProcessConsentAsync(mailboxId.Value, source).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                item.IsProcessConsentGranted = false;
                item.MailboxId = null;
            });
            if (item.LocalAccountId is Guid localAccountId)
            {
                await _semanticIndexCoordinator.DeleteLocalIndexAsync(localAccountId).ConfigureAwait(false);
                var account = await _accountService.GetAccountAsync(localAccountId).ConfigureAwait(false);
                if (account is not null)
                {
                    account.Preferences.IsSemanticIndexingEnabled = false;
                    await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
                }
            }
            await ExecuteUIThread(() =>
            {
                if (item.LocalAccountId is null)
                    Mailboxes.Remove(item);
            });
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
            return false;
        }
        catch (Exception exception)
        {
            await ExecuteUIThread(() => item.ErrorMessage = WinoAccountApiErrorTranslator.Translate(exception.Message));
            return item.IsProcessConsentGranted;
        }
        finally
        {
            await ExecuteUIThread(() => item.IsBusy = false);
        }
    }

    public async Task SetAllProcessConsentsAsync(bool granted)
    {
        await ExecuteUIThread(() => IsBulkBusy = true);
        try
        {
            foreach (var item in Mailboxes.Where(x => x.IsProcessConsentGranted != granted).ToArray())
                await SetProcessConsentAsync(item, granted, ConsentActionSources.BulkConsentPage).ConfigureAwait(false);
        }
        finally
        {
            await ExecuteUIThread(() => IsBulkBusy = false);
        }
    }

    private Task SetErrorAsync(string error)
        => ExecuteUIThread(() =>
        {
            ErrorMessage = WinoAccountApiErrorTranslator.Translate(error);
            OnPropertyChanged(nameof(HasError));
        });

    private static string CreateKey(string address, MailProviderType providerType)
        => $"{address.Trim().ToUpperInvariant()}|{(int)providerType}";

    private static Uri? CreateUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static bool IsCurrent(TransportConsentDto consent)
        => consent.Status == ConsentStatuses.Active && consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;

    private static bool IsCurrent(MailboxProcessConsentDto? consent)
        => consent is not null && consent.Status == ConsentStatuses.Active && consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;
}
