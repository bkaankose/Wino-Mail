using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;

#nullable enable

namespace Wino.Messaging.SyncHost;

public sealed record AccountIdPayload(Guid AccountId);

public sealed record SynchronizationProgressRequestPayload(
    Guid AccountId,
    SynchronizationProgressCategory Category);

public sealed record DownloadMimeMessagePayload(MailCopy MailItem, Guid AccountId);

public sealed record DownloadCalendarAttachmentPayload(
    CalendarItem CalendarItem,
    CalendarAttachment Attachment,
    string LocalFilePath);

public sealed record QueueRequestsPayload(
    Guid AccountId,
    bool TriggerSynchronization,
    IReadOnlyList<SerializedRequestPayload> Requests);

public sealed record SerializedRequestPayload(
    string TypeName,
    string Json);

public sealed record PendingMailOperationPayload(
    Guid AccountId,
    IReadOnlyCollection<Guid> UniqueIds);

public sealed record PendingCalendarOperationPayload(
    Guid AccountId,
    IReadOnlyCollection<Guid> CalendarItemIds);

public sealed record OnlineSearchPayload(
    Guid AccountId,
    string QueryText,
    List<MailItemFolder>? Folders);

public sealed record HostSnapshotPayload(
    IReadOnlyList<AccountSynchronizationProgress> ProgressItems,
    IReadOnlyList<SynchronizationActionItem> PendingActions);

public sealed record FolderSynchronizationEnabledPayload(MailItemFolder MailItemFolder);
