using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Shared display contract for mail list item rendering.
/// Implemented by both single mail and thread mail view models.
/// </summary>
public interface IMailItemDisplayInformation : INotifyPropertyChanged
{
    string Subject { get; }
    string FromName { get; }
    string FromAddress { get; }
    string PreviewText { get; }
    bool IsRead { get; }
    bool IsDraft { get; }
    bool IsLocalDraft { get; }
    bool IsDraftSyncFailed { get; }
    bool ShouldShowDraftSyncWarning { get; }
    string DraftSyncTooltip { get; }
    bool HasAttachments { get; }
    bool IsCalendarEvent { get; }
    bool IsFlagged { get; }
    DateTime CreationDate { get; }
    Guid? ContactPictureFileId { get; }
    bool ThumbnailUpdatedEvent { get; }
    bool IsThreadExpanded { get; }
    AccountContact SenderContact { get; }
    bool HasReadReceiptTracking { get; }
    bool IsReadReceiptAcknowledged { get; }
    string ReadReceiptDisplayText { get; }
    string AccountNickname { get; }
    string AccountColorHex { get; }
    AccountNicknamePosition AccountNicknamePosition { get; }
    /// <summary>
    /// A concrete collection on purpose: list controls bind it, and Native AOT only projects concrete
    /// collection types to WinRT.
    /// </summary>
    ObservableCollection<MailCategory> Categories { get; }
    bool HasCategories { get; }
}
