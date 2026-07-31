using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class MailFiltersPageViewModel(
    IMailFilterService mailFilterService,
    IMailFilterProviderService providerService,
    IAccountService accountService,
    IFolderService folderService,
    IMailDialogService dialogService) : MailBaseViewModel
{
    private readonly IMailFilterService _mailFilterService = mailFilterService;
    private readonly IMailFilterProviderService _providerService = providerService;
    private readonly IAccountService _accountService = accountService;
    private readonly IFolderService _folderService = folderService;
    private readonly IMailDialogService _dialogService = dialogService;

    [ObservableProperty]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public ObservableCollection<MailFilterListItemViewModel> Filters { get; } = [];

    public async Task LoadAsync(object parameter, bool refreshProvider = true)
    {
        var accountId = parameter switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => Guid.Empty
        };

        if (accountId == Guid.Empty)
            return;

        await ExecuteUIThread(() => IsBusy = true);
        try
        {
            var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
            if (account == null)
                return;
            await ExecuteUIThread(() => Account = account);

            if (refreshProvider && _providerService.SupportsProviderFilters(account))
            {
                try
                {
                    await _providerService.GetFiltersAsync(account).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await ExecuteUIThread(() => _dialogService.InfoBarMessage(
                            Translator.MailFilters_ProviderRefreshFailedTitle,
                            ex.Message,
                            InfoBarMessageType.Warning))
                        .ConfigureAwait(false);
                }
            }

            var filters = await _mailFilterService.GetFiltersAsync(accountId).ConfigureAwait(false);
            var folders = await _folderService.GetFoldersAsync(accountId).ConfigureAwait(false);
            var folderNames = folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
                .GroupBy(folder => folder.RemoteFolderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().FolderName, StringComparer.OrdinalIgnoreCase);
            var items = filters.Select(filter => CreateListItem(filter, folderNames)).ToList();

            await ExecuteUIThread(() =>
            {
                Filters.Clear();
                foreach (var item in items)
                    Filters.Add(item);
                IsEmpty = Filters.Count == 0;
            });
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }

    public void CreateFilter()
    {
        if (Account == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.MailFilterEditor_CreateTitle,
            WinoPage.MailFilterEditorPage,
            new MailFilterEditorNavigationParameter(Account.Id)));
    }

    public void EditFilter(MailFilterListItemViewModel item)
    {
        if (Account == null || item?.CanEdit != true)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(
            item.Filter.Name,
            WinoPage.MailFilterEditorPage,
            new MailFilterEditorNavigationParameter(Account.Id, item.Filter.Id)));
    }

    public void DuplicateFilter(MailFilterListItemViewModel item)
    {
        if (Account == null || item == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.MailFilterEditor_DuplicateTitle,
            WinoPage.MailFilterEditorPage,
            new MailFilterEditorNavigationParameter(Account.Id, DuplicateFilterId: item.Filter.Id)));
    }

    public async Task DeleteFilterAsync(MailFilterListItemViewModel item)
    {
        if (Account == null || item == null)
            return;

        var message = item.IsProviderManaged
            ? string.Format(Translator.MailFilters_DeleteProviderConfirmation, item.Filter.Name)
            : string.Format(Translator.MailFilters_DeleteLocalConfirmation, item.Filter.Name);
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            message,
            Translator.MailFilters_DeleteTitle,
            Translator.Buttons_Delete);
        if (!confirmed)
            return;

        try
        {
            if (item.IsProviderManaged)
                await _providerService.DeleteFilterAsync(Account, item.Filter).ConfigureAwait(false);
            else
                await _mailFilterService.DeleteFilterAsync(item.Filter.Id).ConfigureAwait(false);

            await LoadAsync(Account.Id, refreshProvider: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ExecuteUIThread(() =>
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error));
        }
    }

    public async Task SetEnabledAsync(MailFilterListItemViewModel item, bool isEnabled)
    {
        if (Account == null || item?.CanEdit != true || item.Filter.IsEnabled == isEnabled)
            return;

        item.Filter.IsEnabled = isEnabled;
        try
        {
            if (item.IsProviderManaged)
                await _providerService.UpdateFilterAsync(Account, item.Filter).ConfigureAwait(false);
            else
                await _mailFilterService.UpdateFilterAsync(item.Filter).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            item.Filter.IsEnabled = !isEnabled;
            await ExecuteUIThread(() =>
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error));
        }
    }

    public async Task MoveFilterAsync(MailFilterListItemViewModel item, int offset)
    {
        if (Account == null || item?.CanReorder != true || offset == 0)
            return;

        var peers = Filters
            .Where(candidate => candidate.Filter.ManagementType == item.Filter.ManagementType)
            .OrderBy(candidate => candidate.Filter.Sequence)
            .ToList();
        var index = peers.IndexOf(item);
        var targetIndex = index + offset;
        if (index < 0 || targetIndex < 0 || targetIndex >= peers.Count)
            return;

        var target = peers[targetIndex];
        (item.Filter.Sequence, target.Filter.Sequence) = (target.Filter.Sequence, item.Filter.Sequence);
        try
        {
            await SaveSequenceAsync(item).ConfigureAwait(false);
            await SaveSequenceAsync(target).ConfigureAwait(false);
            await LoadAsync(Account.Id, refreshProvider: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ExecuteUIThread(() =>
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error));
        }
    }

    private Task SaveSequenceAsync(MailFilterListItemViewModel item)
        => item.IsProviderManaged
            ? _providerService.UpdateFilterAsync(Account, item.Filter)
            : _mailFilterService.UpdateFilterAsync(item.Filter);

    private MailFilterListItemViewModel CreateListItem(
        MailFilter filter,
        IReadOnlyDictionary<string, string> folderNames)
    {
        var sourceName = string.Empty;
        var hasMissingSource = filter.ManagementType == MailFilterManagementType.WinoLocal
            && (string.IsNullOrWhiteSpace(filter.SourceRemoteFolderId)
                || !folderNames.TryGetValue(filter.SourceRemoteFolderId, out sourceName));
        var missingTarget = filter.Actions
            .FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.TargetRemoteFolderId)
                && !folderNames.ContainsKey(action.TargetRemoteFolderId));
        filter.HasMissingFolder = hasMissingSource || missingTarget != null;
        filter.SourceFolderName = hasMissingSource
            ? Translator.MailFilters_MissingFolder
            : filter.ManagementType == MailFilterManagementType.Provider
                ? Translator.MailFilters_ProviderScope
                : sourceName;

        var providerName = Account.ProviderType == MailProviderType.Outlook ? "Outlook" : "Gmail";
        var management = filter.ManagementType == MailFilterManagementType.Provider
            ? string.Format(Translator.MailFilters_ManagedByProvider, providerName)
            : Translator.MailFilters_ManagedByWino;
        var status = filter.HasMissingFolder
            ? Translator.MailFilters_NeedsAttention
            : filter.ProviderHasError
                ? Translator.MailFilters_ProviderError
                : filter.IsEnabled
                    ? Translator.MailFilters_Enabled
                    : Translator.MailFilters_Disabled;
        var canEdit = filter.ManagementType == MailFilterManagementType.WinoLocal
            || (filter.IsWinoCreated && !filter.IsReadOnly);
        var canReorder = canEdit
            && (filter.ManagementType == MailFilterManagementType.WinoLocal
                || Account.ProviderType == MailProviderType.Outlook);

        return new MailFilterListItemViewModel(
            filter,
            management,
            status,
            BuildRuleSummary(filter),
            canEdit,
            canReorder);
    }

    private static string BuildRuleSummary(MailFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.ProviderSummary) && filter.Conditions.Count == 0 && filter.Actions.Count == 0)
            return filter.ProviderSummary;

        return string.Format(
            Translator.MailFilters_Summary,
            filter.Conditions.Count,
            filter.Actions.Count,
            filter.SourceFolderName);
    }
}

public sealed class MailFilterListItemViewModel(
    MailFilter filter,
    string managementText,
    string statusText,
    string summary,
    bool canEdit,
    bool canReorder)
{
    public MailFilter Filter { get; } = filter;
    public string ManagementText { get; } = managementText;
    public string StatusText { get; } = statusText;
    public string Summary { get; } = summary;
    public bool CanEdit { get; } = canEdit;
    public bool CanReorder { get; } = canReorder;
    public bool IsProviderManaged => Filter.ManagementType == MailFilterManagementType.Provider;
    public bool HasWarning => Filter.HasMissingFolder || Filter.ProviderHasError;
}
