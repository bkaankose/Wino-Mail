#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>
/// The items a context-aware request was started from (right-click, Wino Assistant). It stays on the device: the
/// server only gets the counts, flags and address hashes <see cref="VoiceContextKind"/> allows, and commands with
/// <see cref="VoiceCommand.UsesContext"/> act on these items. Only the list of <see cref="Kind"/> is filled.
/// </summary>
public sealed record VoiceLocalSelection(
    VoiceContextKind Kind,
    IReadOnlyList<MailCopy> Mails,
    IReadOnlyList<CalendarItem> Events,
    IReadOnlyList<AccountTask> Tasks,
    IReadOnlyList<AccountContact> Contacts,
    MailItemFolder? Folder)
{
    public int Count => Kind switch
    {
        VoiceContextKind.Mail => Mails.Count,
        VoiceContextKind.Event => Events.Count,
        VoiceContextKind.Task => Tasks.Count,
        VoiceContextKind.Contact => Contacts.Count,
        _ => Folder is null ? 0 : 1
    };

    public static VoiceLocalSelection ForMails(IReadOnlyList<MailCopy> mails) => new(VoiceContextKind.Mail, mails, Array.Empty<CalendarItem>(), Array.Empty<AccountTask>(), Array.Empty<AccountContact>(), null);

    public static VoiceLocalSelection ForEvents(IReadOnlyList<CalendarItem> events) => new(VoiceContextKind.Event, Array.Empty<MailCopy>(), events, Array.Empty<AccountTask>(), Array.Empty<AccountContact>(), null);

    public static VoiceLocalSelection ForTasks(IReadOnlyList<AccountTask> tasks) => new(VoiceContextKind.Task, Array.Empty<MailCopy>(), Array.Empty<CalendarItem>(), tasks, Array.Empty<AccountContact>(), null);

    public static VoiceLocalSelection ForContacts(IReadOnlyList<AccountContact> contacts) => new(VoiceContextKind.Contact, Array.Empty<MailCopy>(), Array.Empty<CalendarItem>(), Array.Empty<AccountTask>(), contacts, null);

    public static VoiceLocalSelection ForFolder(MailItemFolder folder) => new(VoiceContextKind.Folder, Array.Empty<MailCopy>(), Array.Empty<CalendarItem>(), Array.Empty<AccountTask>(), Array.Empty<AccountContact>(), folder);
}
