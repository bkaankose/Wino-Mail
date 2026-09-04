using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels.Data;

public enum ContactFilterKind
{
    All,
    Favorites,
    AddressBook,
    List
}

/// <summary>
/// A single selectable entry in the contacts navigation pane. Produces the
/// <see cref="ContactQueryFilter"/> the contact list is loaded with, and for list entries
/// also accepts contacts dropped onto it.
/// </summary>
public partial class ContactFilterViewModel : MenuItemBase, IMenuItemDropTarget, IAccountNavigationMenuItem
{
    /// <summary>
    /// Supplied by <see cref="ContactsPageViewModel"/> so the item can service a drop
    /// without the view knowing which view model owns the operation.
    /// </summary>
    internal Func<ContactList, IReadOnlyList<Guid>, Task> DropHandler { get; set; }

    /// <summary>Raised when the rename or delete command is invoked on a list entry.</summary>
    internal Action<ContactFilterViewModel> RenameRequested { get; set; }

    internal Action<ContactFilterViewModel> DeleteRequested { get; set; }
    internal Func<Guid, Task> SynchronizeAccountRequested { get; set; }

    public ContactFilterKind Kind { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName))]
    public partial string Name { get; set; }
    public string Glyph { get; init; }
    public Guid? AddressBookId { get; init; }
    public Guid? AccountId { get; init; }
    public MailAccount Account { get; init; }
    public ContactList List { get; init; }
    public ContactAddressBook AddressBook { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadItemCount))]
    public partial int Count { get; set; }
    [ObservableProperty] public partial bool IsDraggingItemOver { get; set; }

    #region Account navigation presentation

    // Address book entries are drawn by the shell's shared account row, so they read
    // the same as an account does in mail and tasks. Nothing here syncs or needs
    // attention, and the mail-only context actions stay hidden.

    public string AccountName => Name;
    public string AccountAddress => Account?.Address ?? string.Empty;
    public int UnreadItemCount => Count;
    public bool IsSynchronizationProgressVisible => false;
    public bool IsProgressIndeterminate => false;
    public double SynchronizationProgressValue => 0;
    public bool IsAttentionRequired => false;
    public bool SupportsMailAccountActions => false;
    public AccountDetailsTab AccountDetailsTab => global::Wino.Core.Domain.Models.Navigation.AccountDetailsTab.People;
    public bool SupportsAccountSynchronization => HasAccountIcon && Account.IsContactAccessGranted;

    /// <summary>An address book is the destination itself, not a parent of one.</summary>
    public bool SelectsOnInvoked => true;

    public Task SynchronizeAccountAsync()
        => SupportsAccountSynchronization && SynchronizeAccountRequested is not null
            ? SynchronizeAccountRequested(Account.Id)
            : Task.CompletedTask;

    #endregion

    public bool IsList => Kind == ContactFilterKind.List;
    public bool CanManageRemoteAddressBook => AddressBook?.SourceKind == ContactSourceKind.CardDav && !AddressBook.IsReadOnly;
    public bool CanRenameOrDelete => IsList || CanManageRemoteAddressBook;
    public bool HasAccountIcon => Kind == ContactFilterKind.AddressBook && Account is not null;
    public Guid? ListId => List?.Id;

    private ContactFilterViewModel(ContactFilterKind kind) => Kind = kind;

    public static ContactFilterViewModel CreateAll(string name)
        => new(ContactFilterKind.All) { Name = name, Glyph = "" };

    public static ContactFilterViewModel CreateFavorites(string name)
        => new(ContactFilterKind.Favorites) { Name = name, Glyph = "" };

    public static ContactFilterViewModel CreateAddressBook(ContactAddressBook book, MailAccount account)
        => new(ContactFilterKind.AddressBook)
        {
            Name = string.IsNullOrWhiteSpace(book.DisplayName) ? account?.Name ?? book.SourceKind.ToString() : book.DisplayName,
            Glyph = "",
            AddressBookId = book.Id,
            AccountId = book.MailAccountId,
            Account = account,
            AddressBook = book
        };

    public static ContactFilterViewModel CreateList(ContactList list)
        => new(ContactFilterKind.List) { Name = list.Name, Glyph = "", List = list };

    public ContactQueryFilter ToQueryFilter(string searchQuery) => Kind switch
    {
        ContactFilterKind.Favorites => new ContactQueryFilter(FavoritesOnly: true, SearchQuery: searchQuery, ExcludeRootContacts: true),
        ContactFilterKind.AddressBook => new ContactQueryFilter(AddressBookId: AddressBookId, SearchQuery: searchQuery, ExcludeRootContacts: true),
        ContactFilterKind.List => new ContactQueryFilter(ListId: ListId, SearchQuery: searchQuery, ExcludeRootContacts: true),
        _ => new ContactQueryFilter(SearchQuery: searchQuery, ExcludeRootContacts: true)
    };

    #region Drag and drop

    public bool CanAccept(IReadOnlyDictionary<string, object> dataProperties)
        => IsList &&
           DropHandler is not null &&
           TryGetPackage(dataProperties, out var package) &&
           package.ContactIds.Count > 0;

    public string GetDropCaption(IReadOnlyDictionary<string, object> dataProperties)
        => string.Format(Translator.ContactDrag_AddToListCaption, Name);

    public Task HandleDropAsync(IReadOnlyDictionary<string, object> dataProperties)
        => TryGetPackage(dataProperties, out var package) && DropHandler is not null
            ? DropHandler(List, package.ContactIds)
            : Task.CompletedTask;

    private static bool TryGetPackage(IReadOnlyDictionary<string, object> dataProperties, out ContactDragPackage package)
    {
        package = dataProperties.TryGetValue(ContactDragPackage.DataPropertyName, out var value)
            ? value as ContactDragPackage
            : null;

        return package is not null;
    }

    #endregion

    #region Commands

    private bool CanModifyList() => CanRenameOrDelete;

    [RelayCommand(CanExecute = nameof(CanModifyList))]
    private void RenameList() => RenameRequested?.Invoke(this);

    [RelayCommand(CanExecute = nameof(CanModifyList))]
    private void DeleteList() => DeleteRequested?.Invoke(this);

    #endregion
}
