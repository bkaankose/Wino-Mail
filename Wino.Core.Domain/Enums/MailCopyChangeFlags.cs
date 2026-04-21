using System;

namespace Wino.Core.Domain.Enums;

[Flags]
public enum MailCopyChangeFlags
{
    None = 0,
    Id = 1 << 0,
    FolderId = 1 << 1,
    ThreadId = 1 << 2,
    MessageId = 1 << 3,
    References = 1 << 4,
    InReplyTo = 1 << 5,
    FromName = 1 << 6,
    FromAddress = 1 << 7,
    Subject = 1 << 8,
    PreviewText = 1 << 9,
    CreationDate = 1 << 10,
    Importance = 1 << 11,
    IsRead = 1 << 12,
    IsFlagged = 1 << 13,
    IsPinned = 1 << 14,
    IsFocused = 1 << 15,
    HasAttachments = 1 << 16,
    ItemType = 1 << 17,
    DraftId = 1 << 18,
    IsDraft = 1 << 19,
    FileId = 1 << 20,
    AssignedFolder = 1 << 21,
    AssignedAccount = 1 << 22,
    SenderContact = 1 << 23,
    UniqueId = 1 << 24,
    ReadReceiptState = 1 << 25,
    Categories = 1 << 26,
    All = Id |
          FolderId |
          ThreadId |
          MessageId |
          References |
          InReplyTo |
          FromName |
          FromAddress |
          Subject |
          PreviewText |
          CreationDate |
          Importance |
          IsRead |
          IsFlagged |
          IsPinned |
          IsFocused |
          HasAttachments |
          ItemType |
          DraftId |
          IsDraft |
          FileId |
          AssignedFolder |
          AssignedAccount |
          SenderContact |
          UniqueId |
          ReadReceiptState |
          Categories
}
