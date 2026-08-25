using System;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

public record ContactEditNavigationParameter(Guid? ContactId = null, ContactImportDraft? ImportDraft = null);

public sealed record ContactImportDraft(
    AccountContact Contact,
    byte[]? PhotoBytes = null,
    bool HasUnsupportedContent = false);
