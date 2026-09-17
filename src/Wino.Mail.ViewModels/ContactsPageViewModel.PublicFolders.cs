using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Pinned Exchange public contact folders in People. Each pin is a read-only entry of its own section in
/// the pane. Its contacts are read live through <see cref="IPublicFolderService"/> and never reach the
/// contact store, so they cannot collide with the user's own contacts; everything that writes to the
/// store (edit, delete, photo, favorite, lists) is off for them, while "send mail" works.
/// </summary>
public partial class ContactsPageViewModel : IRecipient<PublicFolderFavoritesChanged>
{
    private readonly IPublicFolderService _publicFolderService;
    private readonly IPublicFolderFavoriteService _publicFolderFavoriteService;
    private readonly ContactFilterGroup _publicFolderFilterGroup = [];

    // Pins belong to an Exchange account whether or not that account also synchronizes its own contacts.
    private Dictionary<Guid, MailAccount> _exchangeAccounts = [];

    // The cards of the public folder on screen, kept for the title bar search.
    private List<AccountContact> _publicFolderContacts = [];

    private bool IsPublicFolderSelected => SelectedFilter?.IsPublicFolder == true;

    void IRecipient<PublicFolderFavoritesChanged>.Receive(PublicFolderFavoritesChanged message)
    {
        if (message.Kind == PublicFolderKind.Contacts && _isPageActive)
            _ = RefreshPublicFolderFiltersAsync();
    }

    /// <summary>
    /// Brings the public folder section in line with the pinned contact folders of the Exchange accounts.
    /// Entries are reused by account and folder id so the selection survives.
    /// </summary>
    private void ReconcilePublicFolderFilters()
    {
        var favorites = _publicFolderFavoriteService?.GetFavorites()
            .Where(favorite => favorite.Kind == PublicFolderKind.Contacts &&
                               !string.IsNullOrEmpty(favorite.FolderId) &&
                               _exchangeAccounts.ContainsKey(favorite.AccountId))
            .OrderBy(favorite => _exchangeAccounts[favorite.AccountId].Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(favorite => favorite.Name, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var desired = new List<ContactFilterViewModel>(favorites.Count);

        for (var targetIndex = 0; targetIndex < favorites.Count; targetIndex++)
        {
            var favorite = favorites[targetIndex];
            var filter = _publicFolderFilterGroup.FirstOrDefault(item => IsSamePublicFolder(item, favorite.AccountId, favorite.FolderId));

            if (filter is null)
            {
                filter = ContactFilterViewModel.CreatePublicFolder(favorite, _exchangeAccounts[favorite.AccountId]);
                AttachFilterCallbacks(filter);
                _publicFolderFilterGroup.Insert(Math.Min(targetIndex, _publicFolderFilterGroup.Count), filter);
            }
            else
            {
                filter.Name = favorite.DisplayName;
            }

            MoveToIndex(_publicFolderFilterGroup, filter, targetIndex);
            desired.Add(filter);
        }

        RemoveFiltersExcept(_publicFolderFilterGroup, desired);
    }

    private static bool IsSamePublicFolder(ContactFilterViewModel filter, Guid? accountId, string folderId)
        => filter.IsPublicFolder &&
           filter.AccountId == accountId &&
           string.Equals(filter.PublicFolderId, folderId, StringComparison.Ordinal);

    /// <summary>A pin or unpin made while People is open: rebuild the pane, and reload if the open entry left.</summary>
    private async Task RefreshPublicFolderFiltersAsync()
    {
        var selectedBefore = SelectedFilter;

        await BuildFiltersAsync().ConfigureAwait(false);

        if (!ReferenceEquals(selectedBefore, SelectedFilter))
            await ReloadContactsAsync().ConfigureAwait(false);
    }

    private void UnpinPublicFolder(ContactFilterViewModel filter)
    {
        if (filter is not { IsPublicFolder: true, AccountId: Guid accountId } || _publicFolderFavoriteService is null)
            return;

        // The favorite service announces the change, which rebuilds the pane through the recipient above.
        _publicFolderFavoriteService.RemoveFavorite(accountId, filter.PublicFolderId);
    }

    /// <summary>Loads the selected public folder live. It has no paging: a contact folder arrives whole.</summary>
    private async Task LoadPublicFolderContactsAsync(ContactFilterViewModel filter, int queryVersion)
    {
        IReadOnlyList<PublicFolderContact> contacts = _publicFolderService is null || filter.AccountId is not Guid accountId
            ? Array.Empty<PublicFolderContact>()
            : await _publicFolderService.GetContactsAsync(accountId, filter.PublicFolderId).ConfigureAwait(false);

        if (queryVersion != _currentQueryVersion)
            return;

        var cards = PublicFolderContactMapper.ToAccountContacts(contacts, filter.AccountId.GetValueOrDefault(), filter.PublicFolderId);

        await ExecuteUIThread(() =>
        {
            Contacts.Clear();
            ContactGroups.Clear();
            _publicFolderContacts = cards;

            foreach (var card in cards)
            {
                var item = CreatePublicFolderContactViewModel(card, filter);
                Contacts.Add(item);
                AppendToGroup(item);
            }

            TotalContactsCount = cards.Count;
            HasMoreContacts = false;
            _currentOffset = Contacts.Count;
        });
    }

    private IReadOnlyList<AccountContactViewModel> SearchPublicFolderContacts(ContactFilterViewModel filter, string search, int limit)
        => _publicFolderContacts
            .Where(card => Contains(card.DisplayName, search) ||
                           Contains(card.PrimaryEmailAddress, search) ||
                           Contains(card.CompanyName, search) ||
                           Contains(card.PrimaryPhoneNumber, search))
            .Take(Math.Max(1, limit))
            .Select(card => CreatePublicFolderContactViewModel(card, filter))
            .ToList();

    private static bool Contains(string value, string search)
        => !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private AccountContactViewModel CreatePublicFolderContactViewModel(AccountContact card, ContactFilterViewModel filter)
        => new(
            card,
            filter.Name,
            isAuthorized: false,
            _preferencesService?.ContactNameDisplayFormat ?? ContactNameDisplayFormat.ProviderDisplayName,
            _preferencesService?.ContactSortOrder ?? ContactSortOrder.ProviderDisplayName)
        {
            IsReadOnlySource = true
        };
}
