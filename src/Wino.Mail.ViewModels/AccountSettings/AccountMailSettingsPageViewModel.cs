#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>Every mail setting of one account, each saved as soon as it changes.</summary>
public partial class AccountMailSettingsPageViewModel : AccountAppPageViewModelBase, IRecipient<WinoIntelligenceEntitlementChanged>
{
    private static readonly int[] Pop3DeleteAfterDaysOptions = [0, 7, 14, 30, 60, 90];
    private static readonly TimeSpan SenderNameSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly IFolderService _folderService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IMailProviderRegistry _providers;
    private readonly IWinoAccountIntelligenceSnapshotService? _entitlementService;

    private bool _isLoadingContent;
    private CancellationTokenSource? _senderNameSaveCancellation;

    public AccountMailSettingsPageViewModel(
        IMessenger messenger,
        IAccountService accountService,
        IMailDialogService dialogService,
        IAccountCapabilityService capabilityService,
        AccountAppChangeCoordinator appChanges,
        IFolderService folderService,
        INotificationBuilder notificationBuilder,
        ISynchronizationManager synchronizationManager,
        IMailProviderRegistry providers,
        IWinoAccountIntelligenceSnapshotService? entitlementService = null)
        : base(messenger, WinoApplicationMode.Mail, accountService, dialogService, capabilityService, appChanges)
    {
        _folderService = folderService;
        _notificationBuilder = notificationBuilder;
        _synchronizationManager = synchronizationManager;
        _providers = providers;
        _entitlementService = entitlementService;
        CanAccessWinoIntelligence = entitlementService?.CurrentEntitlement.CanAccessSurfaces == true;
    }

    public ObservableCollection<IMailItemFolder> CurrentFolders { get; } = [];

    public List<string> Pop3DeleteAfterDaysLabels { get; } = Pop3DeleteAfterDaysOptions
        .Select(days => days == 0
            ? Translator.POP3Setup_DeleteAfterDays_Never
            : string.Format(Translator.POP3Setup_DeleteAfterDays_Days, days))
        .ToList();

    [ObservableProperty]
    public partial bool CanAccessWinoIntelligence { get; set; }

    [ObservableProperty]
    public partial string SenderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFocusedInboxEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsSignatureEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAppendMessageSettingEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsJumpListEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsNotificationContentHidden { get; set; }

    [ObservableProperty]
    public partial bool Pop3LeaveOnServer { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedPop3DeleteAfterDaysIndex { get; set; }

    [ObservableProperty]
    public partial bool Pop3DeleteWithLocal { get; set; } = true;

    /// <summary>
    /// Only IMAP and POP3 build the From header locally. Outlook and Gmail use the provider's name, so the field is
    /// replaced by a note there; per-alias names stay editable under Aliases.
    /// </summary>
    public bool IsSenderNameEditable => Account?.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3;

    public bool IsProviderManagedSenderNameNoteVisible => Account != null && !IsSenderNameEditable;

    public bool IsFocusedInboxSupported => Account?.Preferences?.IsFocusedInboxEnabled != null;

    public bool IsAppendMessageSettingVisible => Account?.ProviderType == MailProviderType.IMAP4;

    public bool IsPop3 => Account?.ProviderType == MailProviderType.POP3;

    public string Address => Account?.Address ?? string.Empty;

    public bool IsInitialSynchronizationSummaryVisible => Account?.CreatedAt.HasValue == true &&
                                                          Account.InitialSynchronizationRange != InitialSynchronizationRange.Everything;

    public string InitialSynchronizationSummary => Account?.CreatedAt is not DateTime createdAtUtc
        ? string.Empty
        : Account.InitialSynchronizationRange.ToCutoffDateUtc(createdAtUtc) is not DateTime cutoffDateUtc
            ? string.Empty
            : string.Format(
                Translator.AccountDetailsPage_InitialSynchronization_Description,
                cutoffDateUtc.ToLocalTime().ToString("D", CultureInfo.CurrentUICulture));

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        Messenger.Register<WinoIntelligenceEntitlementChanged>(this);

        if (_entitlementService is not null)
            _ = RefreshEntitlementAsync();
    }

    protected override void UnregisterRecipients()
    {
        Messenger.Unregister<WinoIntelligenceEntitlementChanged>(this);
        base.UnregisterRecipients();
    }

    public void Receive(WinoIntelligenceEntitlementChanged message)
        => _ = ExecuteUIThread(() => CanAccessWinoIntelligence = message.Entitlement.CanAccessSurfaces);

    private async Task RefreshEntitlementAsync()
    {
        var entitlement = await _entitlementService!.GetEntitlementAsync().ConfigureAwait(false);
        await ExecuteUIThread(() => CanAccessWinoIntelligence = entitlement.CanAccessSurfaces);
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        if (_senderNameSaveCancellation is { IsCancellationRequested: false } pending)
        {
            pending.Cancel();
            _ = SaveSenderNameAsync();
        }

        base.OnNavigatedFrom(mode, parameters);
    }

    protected override void OnAccountApplied(MailAccount account)
    {
        OnPropertyChanged(nameof(IsSenderNameEditable));
        OnPropertyChanged(nameof(IsProviderManagedSenderNameNoteVisible));
        OnPropertyChanged(nameof(IsFocusedInboxSupported));
        OnPropertyChanged(nameof(IsAppendMessageSettingVisible));
        OnPropertyChanged(nameof(IsPop3));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(IsInitialSynchronizationSummaryVisible));
        OnPropertyChanged(nameof(InitialSynchronizationSummary));
    }

    protected override async Task LoadContentAsync(MailAccount account)
    {
        var folders = account.IsMailAccessGranted
            ? (await _folderService.GetFolderStructureForAccountAsync(account.Id, false)).Folders
            : [];

        await ExecuteUIThread(() =>
        {
            _isLoadingContent = true;

            try
            {
                SenderName = account.SenderName ?? string.Empty;
                IsFocusedInboxEnabled = account.Preferences?.IsFocusedInboxEnabled.GetValueOrDefault() == true;
                IsSignatureEnabled = account.Preferences?.IsSignatureEnabled == true;
                IsAppendMessageSettingEnabled = account.Preferences?.ShouldAppendMessagesToSentFolder == true;
                IsJumpListEnabled = account.Preferences?.IsJumpListEnabled == true;
                IsNotificationContentHidden = account.Preferences?.HideNotificationContent == true;
                ApplyPop3Options(account.ServerInformation);

                CurrentFolders.Clear();
                foreach (var folder in folders)
                    CurrentFolders.Add(folder);
            }
            finally
            {
                _isLoadingContent = false;
            }
        });
    }

    private void ApplyPop3Options(CustomServerInformation? server)
    {
        if (server == null)
            return;

        Pop3LeaveOnServer = !server.Pop3DeleteFromServerAfterDownload;
        Pop3DeleteWithLocal = !server.Pop3KeepOnServerWhenDeletedLocally;

        // A value not in the list maps to the closest longer option, so nothing is removed sooner.
        var days = Math.Max(0, server.Pop3DeleteFromServerAfterDays);
        var index = Array.FindIndex(Pop3DeleteAfterDaysOptions, option => option >= days && (option > 0 || days == 0));
        SelectedPop3DeleteAfterDaysIndex = index < 0 ? Pop3DeleteAfterDaysOptions.Length - 1 : index;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isLoadingContent || Account is not { } account)
            return;

        switch (e.PropertyName)
        {
            case nameof(SenderName):
                _senderNameSaveCancellation?.Cancel();
                _senderNameSaveCancellation = new CancellationTokenSource();
                _ = SaveSenderNameAfterDelayAsync(_senderNameSaveCancellation.Token);
                break;
            case nameof(IsFocusedInboxEnabled) when IsFocusedInboxSupported:
                _ = SavePreferenceAsync(account, preferences => preferences.IsFocusedInboxEnabled = IsFocusedInboxEnabled, updateBadge: true);
                break;
            case nameof(IsSignatureEnabled):
                _ = SavePreferenceAsync(account, preferences => preferences.IsSignatureEnabled = IsSignatureEnabled);
                break;
            case nameof(IsAppendMessageSettingEnabled):
                _ = SavePreferenceAsync(account, preferences => preferences.ShouldAppendMessagesToSentFolder = IsAppendMessageSettingEnabled);
                break;
            case nameof(IsJumpListEnabled):
                _ = SavePreferenceAsync(account, preferences => preferences.IsJumpListEnabled = IsJumpListEnabled);
                break;
            case nameof(IsNotificationContentHidden):
                _ = SavePreferenceAsync(account, preferences => preferences.HideNotificationContent = IsNotificationContentHidden);
                break;
            case nameof(Pop3LeaveOnServer) or nameof(SelectedPop3DeleteAfterDaysIndex) or nameof(Pop3DeleteWithLocal) when IsPop3:
                _ = SavePop3OptionsAsync(account);
                break;
        }
    }

    private async Task SavePreferenceAsync(MailAccount account, Action<MailAccountPreferences> apply, bool updateBadge = false)
    {
        try
        {
            if (account.Preferences == null)
                return;

            apply(account.Preferences);
            await AccountService.UpdateAccountAsync(account);

            if (updateBadge)
                await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
        }
        catch (Exception)
        {
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }

    private async Task SaveSenderNameAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SenderNameSaveDelay, cancellationToken);
            await SaveSenderNameAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveSenderNameAsync()
    {
        var account = Account;
        var senderName = SenderName?.Trim() ?? string.Empty;
        if (account == null || !IsSenderNameEditable || senderName == (account.SenderName ?? string.Empty))
            return;

        try
        {
            account.SenderName = senderName;
            await AccountService.UpdateAccountAsync(account);
        }
        catch (Exception)
        {
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }

    /// <summary>
    /// These choices only decide when Wino deletes downloaded messages from the server, so they need no connection test.
    /// The synchronizer reads them when it is created.
    /// </summary>
    private async Task SavePop3OptionsAsync(MailAccount account)
    {
        if (account.ServerInformation is not { } server)
            return;

        var days = SelectedPop3DeleteAfterDaysIndex >= 0 && SelectedPop3DeleteAfterDaysIndex < Pop3DeleteAfterDaysOptions.Length
            ? Pop3DeleteAfterDaysOptions[SelectedPop3DeleteAfterDaysIndex]
            : 0;

        var updated = server.CloneForCredentialStorage();
        updated.Pop3DeleteFromServerAfterDownload = !Pop3LeaveOnServer;
        updated.Pop3DeleteFromServerAfterDays = days;
        updated.Pop3KeepOnServerWhenDeletedLocally = !Pop3DeleteWithLocal;

        try
        {
            await AccountService.UpdateAccountCustomServerInformationAsync(updated);
            account.ServerInformation = updated;
            await _synchronizationManager.DestroySynchronizerAsync(account.Id);
        }
        catch (Exception)
        {
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }

    public Task FolderSyncToggledAsync(IMailItemFolder folder, bool isEnabled)
        => _folderService.ChangeFolderSynchronizationStateAsync(folder.Id, isEnabled);

    public async Task FolderJumpListToggledAsync(IMailItemFolder folder, bool isEnabled)
    {
        await _folderService.ChangeFolderJumpListStateAsync(folder.Id, isEnabled);
        folder.IsJumpListEnabled = isEnabled;
        await _notificationBuilder.UpdateJumpListOptionsAsync();
    }

    [RelayCommand]
    private Task SetupSpecialFolders()
        => Account == null ? Task.CompletedTask : DialogService.HandleSystemFolderConfigurationDialogAsync(Account.Id, _folderService);

    [RelayCommand]
    private void EditSignature() => NavigateWithAccount(Translator.SettingsSignature_Title, WinoPage.SignatureManagementPage);

    [RelayCommand]
    private void ManageMailFilters() => NavigateWithAccount(Translator.MailFilters_Title, WinoPage.MailFiltersPage);

    [RelayCommand]
    private void EditAliases() => NavigateWithAccount(Translator.SettingsManageAliases_Title, WinoPage.AliasManagementPage);

    [RelayCommand]
    private void EditCategories() => NavigateWithAccount(Translator.MailCategoryManagementPage_Title, WinoPage.MailCategoryManagementPage);

    [RelayCommand]
    private void CustomizeFolderList() => NavigateWithAccount(Translator.FolderCustomization_Title, WinoPage.FolderCustomizationPage);

    [RelayCommand]
    private void ConfigureUnreadBadges()
    {
        if (Account != null)
            NavigateWithAccount(string.Format(Translator.UnreadBadges_AccountSettingsTitleFormat, Account.Name), WinoPage.AccountUnreadBadgePage);
    }

    [RelayCommand]
    private void ManageWinoIntelligence()
    {
        if (CanAccessWinoIntelligence)
            NavigateWithAccount(Translator.SemanticIndex_PageTitle, WinoPage.WinoIntelligenceManagementPage);
    }

    private void NavigateWithAccount(string title, WinoPage page)
    {
        if (Account != null)
            Messenger.Send(new BreadcrumbNavigationRequested(title, page, Account.Id));
    }
}
