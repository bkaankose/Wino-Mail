using System;
using System.ComponentModel;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

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
    bool HasAttachments { get; }
    bool IsCalendarEvent { get; }
    bool IsFlagged { get; }
    DateTime CreationDate { get; }
    Guid? ContactPictureFileId { get; }
    bool ThumbnailUpdatedEvent { get; }
    bool IsThreadExpanded { get; }
    AccountContact SenderContact { get; }
}
