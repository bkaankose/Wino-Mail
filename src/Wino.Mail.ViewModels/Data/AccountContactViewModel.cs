using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
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
    public Guid Id => SourceContact.Id;
    public string SecondaryValue => SourceContact.PrimaryEmailAddress ?? SourceContact.PrimaryPhoneNumber ?? string.Empty;
    public string SourceLabel { get; }
    public bool IsEditable { get; }

    /// <summary>
    /// Local-only favorite marker. Writes through to the underlying contact so that a
    /// toggle is reflected without reloading the page.
    /// </summary>
    public bool IsFavorite
    {
        get => SourceContact.IsFavorite;
        set
        {
            if (SourceContact.IsFavorite == value) return;
            SourceContact.IsFavorite = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// "Job title · Company", or whichever of the two is present.
    /// </summary>
    public string JobTitleOrCompany
        => string.Join(" · ", new[] { SourceContact.JobTitle, SourceContact.CompanyName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>
    /// Alphabetical section key. Non-letters collapse into a single "#" section.
    /// </summary>
    public string InitialLetter
    {
        get
        {
            var source = SourceContact.SortKey;
            if (string.IsNullOrWhiteSpace(source))
                source = SourceContact.DisplayValue;

            var first = source?.TrimStart().FirstOrDefault() ?? '#';
            return char.IsLetter(first) ? char.ToUpperInvariant(first).ToString() : "#";
        }
    }

    public AccountContactViewModel(AccountContact contact, string accountName = null, bool isAuthorized = true)
    {
        SourceContact = contact;
        Address = contact.Address;
        Name = contact.Name;
        ContactPictureFileId = contact.ContactPictureFileId;
        IsRootContact = contact.IsRootContact;
        IsOverridden = contact.IsOverridden;
        SourceLabel = string.IsNullOrWhiteSpace(accountName) ? contact.SourceKind.ToString() : $"{accountName} · {contact.SourceKind}";
        IsEditable = contact.SourceKind == ContactSourceKind.Local || isAuthorized;
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
    public string ShortDisplayName => Address == Name || string.IsNullOrWhiteSpace(Name) ? Address?.ToLowerInvariant() ?? SourceContact.DisplayValue : Name;

    /// <summary>
    /// Display name of the contact in a format: Name <Address>.
    /// </summary>
    public string DisplayName => Address == Name || string.IsNullOrWhiteSpace(Name) ? Address?.ToLowerInvariant() ?? SourceContact.DisplayValue : string.IsNullOrWhiteSpace(Address) ? Name : $"{Name} <{Address.ToLowerInvariant()}>";

    [ObservableProperty]
    public partial bool ThumbnailUpdatedEvent { get; set; }

    // IMailItemDisplayInformation implementation for avatar-only rendering.
    public string Subject => string.Empty;
    public string FromName => Name ?? string.Empty;
    public string FromAddress => Address ?? string.Empty;
    public string PreviewText => string.Empty;
    public bool IsRead => true;
    public bool IsDraft => false;
    public bool IsLocalDraft => false;
    public bool IsDraftSyncFailed => false;
    public bool ShouldShowDraftSyncWarning => false;
    public string DraftSyncTooltip => string.Empty;
    public bool HasAttachments => false;
    public bool IsCalendarEvent => false;
    public bool IsFlagged => false;
    public DateTime CreationDate => default;
    public bool IsBusy => false;
    public bool IsThreadExpanded => false;
    public bool HasReadReceiptTracking => false;
    public bool IsReadReceiptAcknowledged => false;
    public string ReadReceiptDisplayText => string.Empty;
    public string AccountNickname => string.Empty;
    public string AccountColorHex => string.Empty;
    public AccountNicknamePosition AccountNicknamePosition => Wino.Core.Domain.Enums.AccountNicknamePosition.None;
    public IReadOnlyList<MailCategory> Categories => [];
    public bool HasCategories => false;
    public AccountContact SenderContact => SourceContact;
}
