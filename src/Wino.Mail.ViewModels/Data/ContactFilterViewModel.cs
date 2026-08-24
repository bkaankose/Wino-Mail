using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Mail.ViewModels.Data;

public enum ContactFilterKind
{
    All,
    Favorites,
    AddressBook,
    List
}

/// <summary>
/// A single selectable entry in the contacts sidebar. Produces the
/// <see cref="ContactQueryFilter"/> the contact list is loaded with.
/// </summary>
public partial class ContactFilterViewModel : ObservableObject
{
    public ContactFilterKind Kind { get; }
    [ObservableProperty] public partial string Name { get; set; }
    public string Glyph { get; init; }
    public Guid? AddressBookId { get; init; }
    public Guid? AccountId { get; init; }
    public MailAccount Account { get; init; }
    public ContactList List { get; init; }

    [ObservableProperty] public partial int Count { get; set; }
    [ObservableProperty] public partial bool IsDragOver { get; set; }

    public bool IsList => Kind == ContactFilterKind.List;
    public bool HasAccountIcon => Kind == ContactFilterKind.AddressBook && Account is not null;
    public bool HasGlyphIcon => !HasAccountIcon;
    public Guid? ListId => List?.Id;

    private ContactFilterViewModel(ContactFilterKind kind) => Kind = kind;

    public static ContactFilterViewModel CreateAll(string name)
        => new(ContactFilterKind.All) { Name = name, Glyph = "\uE716" };

    public static ContactFilterViewModel CreateFavorites(string name)
        => new(ContactFilterKind.Favorites) { Name = name, Glyph = "\uE734" };

    public static ContactFilterViewModel CreateAddressBook(ContactAddressBook book, MailAccount account)
        => new(ContactFilterKind.AddressBook)
        {
            Name = string.IsNullOrWhiteSpace(book.DisplayName) ? account?.Name ?? book.SourceKind.ToString() : book.DisplayName,
            Glyph = "\uE8F1",
            AddressBookId = book.Id,
            AccountId = book.MailAccountId,
            Account = account
        };

    public static ContactFilterViewModel CreateList(ContactList list)
        => new(ContactFilterKind.List) { Name = list.Name, Glyph = "\uE8FD", List = list };

    public ContactQueryFilter ToQueryFilter(string searchQuery) => Kind switch
    {
        ContactFilterKind.Favorites => new ContactQueryFilter(FavoritesOnly: true, SearchQuery: searchQuery, ExcludeRootContacts: true),
        ContactFilterKind.AddressBook => new ContactQueryFilter(AddressBookId: AddressBookId, SearchQuery: searchQuery, ExcludeRootContacts: true),
        ContactFilterKind.List => new ContactQueryFilter(ListId: ListId, SearchQuery: searchQuery, ExcludeRootContacts: true),
        _ => new ContactQueryFilter(SearchQuery: searchQuery, ExcludeRootContacts: true)
    };
}
