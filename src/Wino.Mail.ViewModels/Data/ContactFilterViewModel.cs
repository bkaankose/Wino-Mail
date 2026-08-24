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
    public string Name { get; init; }
    public string Glyph { get; init; }
    public Guid? AddressBookId { get; init; }
    public Guid? AccountId { get; init; }
    public ContactList List { get; init; }

    [ObservableProperty] public partial int Count { get; set; }

    public bool IsList => Kind == ContactFilterKind.List;
    public Guid? ListId => List?.Id;

    private ContactFilterViewModel(ContactFilterKind kind) => Kind = kind;

    public static ContactFilterViewModel CreateAll(string name)
        => new(ContactFilterKind.All) { Name = name, Glyph = "\uE716" };

    public static ContactFilterViewModel CreateFavorites(string name)
        => new(ContactFilterKind.Favorites) { Name = name, Glyph = "\uE734" };

    public static ContactFilterViewModel CreateAddressBook(ContactAddressBook book, string fallbackName)
        => new(ContactFilterKind.AddressBook)
        {
            Name = string.IsNullOrWhiteSpace(book.DisplayName) ? fallbackName : book.DisplayName,
            Glyph = "\uE8F1",
            AddressBookId = book.Id,
            AccountId = book.MailAccountId
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
