#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>
/// The items a context-aware request was started from (right-click, Wino Assistant). It stays on the device: the
/// server only gets the kind, the count and the flags of <see cref="AssistantSelectionFlags"/>; the runtime gets the
/// ids and the senders so it can resolve "the sender" locally. Only the list of <see cref="Kind"/> is filled.
/// </summary>
public sealed record VoiceLocalSelection(
    AssistantSelectionKind Kind,
    IReadOnlyList<MailCopy> Mails,
    IReadOnlyList<CalendarItem> Events,
    IReadOnlyList<AccountTask> Tasks,
    IReadOnlyList<AccountContact> Contacts,
    MailItemFolder? Folder)
{
    public int Count => Kind switch
    {
        AssistantSelectionKind.Mail => Mails.Count,
        AssistantSelectionKind.Event => Events.Count,
        AssistantSelectionKind.Task => Tasks.Count,
        AssistantSelectionKind.Contact => Contacts.Count,
        _ => Folder is null ? 0 : 1
    };

    public static VoiceLocalSelection ForMails(IReadOnlyList<MailCopy> mails) => new(AssistantSelectionKind.Mail, mails, Array.Empty<CalendarItem>(), Array.Empty<AccountTask>(), Array.Empty<AccountContact>(), null);

    public static VoiceLocalSelection ForEvents(IReadOnlyList<CalendarItem> events) => new(AssistantSelectionKind.Event, Array.Empty<MailCopy>(), events, Array.Empty<AccountTask>(), Array.Empty<AccountContact>(), null);

    public static VoiceLocalSelection ForTasks(IReadOnlyList<AccountTask> tasks) => new(AssistantSelectionKind.Task, Array.Empty<MailCopy>(), Array.Empty<CalendarItem>(), tasks, Array.Empty<AccountContact>(), null);

    public static VoiceLocalSelection ForContacts(IReadOnlyList<AccountContact> contacts) => new(AssistantSelectionKind.Contact, Array.Empty<MailCopy>(), Array.Empty<CalendarItem>(), Array.Empty<AccountTask>(), contacts, null);

    public static VoiceLocalSelection ForFolder(MailItemFolder folder) => new(AssistantSelectionKind.Folder, Array.Empty<MailCopy>(), Array.Empty<CalendarItem>(), Array.Empty<AccountTask>(), Array.Empty<AccountContact>(), folder);
}
