using System;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Models.Contacts;

public record ContactEditNavigationParameter(Guid? ContactId = null, ContactImportDraft? ImportDraft = null);

public sealed record ContactImportDraft(
    AccountContact Contact,
    byte[]? PhotoBytes = null,
    bool HasUnsupportedContent = false);
