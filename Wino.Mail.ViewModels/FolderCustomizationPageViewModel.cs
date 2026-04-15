using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Backs the per-account Folder Customization page — lets the user reorder,
/// pin/unpin, and hide folders for a single real (non-merged) account.
/// </summary>
public partial class FolderCustomizationPageViewModel : MailBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IFolderService _folderService;
    private readonly IAccountService _accountService;

    private static readonly SpecialFolderType[] GmailCategorySubTypes =
    [
        SpecialFolderType.Promotions,
        SpecialFolderType.Social,
        SpecialFolderType.Updates,
        SpecialFolderType.Forums,
        SpecialFolderType.Personal
    ];

    private Guid _accountId;
    private bool _isLoaded;

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial bool IsGmailAccount { get; set; }

    public ObservableCollection<FolderCustomizationItemViewModel> PinnedFolders { get; } = [];
    public ObservableCollection<FolderCustomizationItemViewModel> CategoryFolders { get; } = [];
    public ObservableCollection<FolderCustomizationItemViewModel> MoreFolders { get; } = [];

    public FolderCustomizationPageViewModel(IMailDialogService dialogService,
                                            IFolderService folderService,
                                            IAccountService accountService)
    {
        _dialogService = dialogService;
        _folderService = folderService;
        _accountService = accountService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not Guid accountId)
            return;

        _accountId = accountId;

        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null) return;

        AccountName = account.Name;
        IsGmailAccount = account.ProviderType == MailProviderType.Gmail;

        await LoadFoldersAsync();
        _isLoaded = true;
    }

    private async Task LoadFoldersAsync()
    {
        PinnedFolders.Clear();
        CategoryFolders.Clear();
        MoreFolders.Clear();

        var allFolders = await _folderService.GetFoldersAsync(_accountId);

        // Skip the Gmail "Categories" virtual bucket entity — Categories are rendered
        // as an inline section, not as a regular folder row.
        foreach (var folder in allFolders.Where(f => f.SpecialFolderType != SpecialFolderType.Category))
        {
            var item = new FolderCustomizationItemViewModel(folder);

            if (IsGmailAccount && GmailCategorySubTypes.Contains(folder.SpecialFolderType))
            {
                CategoryFolders.Add(item);
            }
            else if (folder.IsSticky)
            {
                PinnedFolders.Add(item);
            }
            else
            {
                MoreFolders.Add(item);
            }
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Translator.FolderCustomization_ResetConfirmMessage,
            Translator.FolderCustomization_ResetConfirmTitle,
            Translator.FolderCustomization_Reset);

        if (!confirmed) return;

        await _folderService.ResetFolderCustomizationAsync(_accountId);
        await LoadFoldersAsync();
    }

    /// <summary>
    /// Called by the view after a drag-reorder or pin/unpin change. Persists the
    /// complete new layout and hidden state for this account.
    /// </summary>
    public async Task PersistLayoutAsync()
    {
        if (!_isLoaded) return;

        // Reconcile IsSticky: Pinned rows become sticky, everything in More loses sticky.
        // Categories (Gmail virtual group children) keep their own rules.
        var touchedFolders = new List<MailItemFolder>();

        foreach (var item in PinnedFolders)
        {
            if (!item.Folder.IsSticky)
            {
                item.Folder.IsSticky = true;
                touchedFolders.Add(item.Folder);
            }
        }

        foreach (var item in MoreFolders)
        {
            if (item.Folder.IsSticky)
            {
                item.Folder.IsSticky = false;
                touchedFolders.Add(item.Folder);
            }
        }

        foreach (var folder in touchedFolders)
        {
            await _folderService.ChangeStickyStatusAsync(folder.Id, folder.IsSticky);
        }

        // Persist the new order: Pinned first, then Categories, then More. The
        // concrete number assigned is only meaningful relative to others in the
        // same account; we still number them globally for simplicity.
        var orderedIds = new List<Guid>();
        orderedIds.AddRange(PinnedFolders.Select(a => a.Folder.Id));
        orderedIds.AddRange(CategoryFolders.Select(a => a.Folder.Id));
        orderedIds.AddRange(MoreFolders.Select(a => a.Folder.Id));

        await _folderService.UpdateFolderOrdersAsync(_accountId, orderedIds);
    }

    public async Task ToggleHiddenAsync(FolderCustomizationItemViewModel item)
    {
        if (item == null) return;

        item.IsHidden = !item.IsHidden;
        item.Folder.IsHidden = item.IsHidden;

        await _folderService.ChangeFolderHiddenStatusAsync(item.Folder.Id, item.IsHidden);
    }

    public async Task TogglePinAsync(FolderCustomizationItemViewModel item)
    {
        if (item == null) return;

        // Categories sub-items cannot be pinned individually; they always travel
        // with the virtual Categories group.
        if (CategoryFolders.Contains(item)) return;

        if (PinnedFolders.Contains(item))
        {
            PinnedFolders.Remove(item);
            MoreFolders.Insert(0, item);
        }
        else if (MoreFolders.Contains(item))
        {
            MoreFolders.Remove(item);
            PinnedFolders.Add(item);
        }

        await PersistLayoutAsync();
    }
}
