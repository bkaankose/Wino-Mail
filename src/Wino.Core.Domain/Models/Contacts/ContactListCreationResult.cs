using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>The name and owning account picked for a new contact list.</summary>
public sealed record ContactListCreationResult(string Name, MailAccount Account);
