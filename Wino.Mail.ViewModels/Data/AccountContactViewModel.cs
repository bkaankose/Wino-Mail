using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data;

public partial class AccountContactViewModel : ObservableObject, IMailItemDisplayInformation
{
    public AccountContact SourceContact { get; }
    public string Address { get; set; }
    public string Name { get; set; }
    public Guid? ContactPictureFileId { get; set; }
    public bool IsRootContact { get; set; }
    public bool IsOverridden { get; set; }

    public AccountContactViewModel(AccountContact contact)
    {
        SourceContact = contact;
        Address = contact.Address;
        Name = contact.Name;
        ContactPictureFileId = contact.ContactPictureFileId;
        IsRootContact = contact.IsRootContact;
        IsOverridden = contact.IsOverridden;
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

    // IMailItemDisplayInformation implementation for avatar-only rendering.
    public string Subject => string.Empty;
    public string FromName => Name ?? string.Empty;
    public string FromAddress => Address ?? string.Empty;
    public string PreviewText => string.Empty;
    public bool IsRead => true;
    public bool IsDraft => false;
    public bool HasAttachments => false;
    public bool IsCalendarEvent => false;
    public bool IsFlagged => false;
    public DateTime CreationDate => default;
    public bool IsBusy => false;
    public bool IsThreadExpanded => false;
    public bool HasReadReceiptTracking => false;
    public bool IsReadReceiptAcknowledged => false;
    public string ReadReceiptDisplayText => string.Empty;
    public IReadOnlyList<MailCategory> Categories => [];
    public bool HasCategories => false;
    public AccountContact SenderContact => new()
    {
        Address = Address,
        Name = Name,
        ContactPictureFileId = ContactPictureFileId,
        IsRootContact = IsRootContact,
        IsOverridden = IsOverridden
    };
}
