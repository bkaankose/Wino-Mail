using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Mail.ViewModels.Data;

public partial class AccountContactViewModel : ObservableObject
{
    public string Address { get; set; }
    public string Name { get; set; }
    public string Base64ContactPicture { get; set; }
    public bool IsRootContact { get; set; }

    public AccountContactViewModel(AccountContact contact)
    {
        Address = contact.Address;
        Name = contact.Name;
        Base64ContactPicture = contact.Base64ContactPicture;
        IsRootContact = contact.IsRootContact;
    }

    /// <summary>
    /// Gets or sets whether the contact is the current account.
    /// </summary>
    public bool IsMe { get; set; }

    /// <summary>
    /// Gets or sets whether the ShortNameOrYOu should have semicolon.
    /// </summary>
    public bool IsSemicolon { get; set; } = true;

    /// <summary>
    /// Provides a short name of the contact.
    /// <see cref="ShortDisplayName"/> or "You"
    /// </summary>
    public string ShortNameOrYou => (IsMe ? Translator.AccountContactNameYou : ShortDisplayName) + (IsSemicolon ? ";" : string.Empty);

    /// <summary>
    /// Short display name of the contact.
    /// Either Name or Address.
    /// </summary>
    public string ShortDisplayName => Address == Name || string.IsNullOrWhiteSpace(Name) ? $"{Address.ToLowerInvariant()}" : $"{Name}";

    /// <summary>
    /// Display name of the contact in a format: Name <Address>.
    /// </summary>
    public string DisplayName => Address == Name || string.IsNullOrWhiteSpace(Name) ? Address.ToLowerInvariant() : $"{Name} <{Address.ToLowerInvariant()}>";

    [ObservableProperty]
    public partial bool ThumbnailUpdatedEvent { get; set; }
}
