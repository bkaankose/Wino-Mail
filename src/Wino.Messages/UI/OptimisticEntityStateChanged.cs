using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public enum OptimisticEntityChange
{
    Upsert,
    Delete
}

/// <summary>Updates the loaded contact projection without persisting the entity.</summary>
public sealed record ContactStateChanged(
    AccountContact Contact,
    OptimisticEntityChange Change,
    EntityUpdateSource Source) : UIMessageBase<ContactStateChanged>;

/// <summary>Updates the loaded task projection without persisting the entity.</summary>
public sealed record TaskStateChanged(
    TaskSynchronizerOperation Operation,
    AccountTaskList List,
    AccountTask Task,
    AccountTaskStep Step,
    OptimisticEntityChange Change,
    EntityUpdateSource Source) : UIMessageBase<TaskStateChanged>;

/// <summary>Updates global contact-list presentation for the application-local request lane.</summary>
public sealed record ContactListStateChanged(
    ContactList List,
    OptimisticEntityChange Change,
    EntityUpdateSource Source) : UIMessageBase<ContactListStateChanged>;

/// <summary>Updates contact-list membership presentation without writing storage.</summary>
public sealed record ContactListMembershipStateChanged(
    Guid ListId,
    IReadOnlyList<Guid> ContactIds,
    bool IsMember,
    EntityUpdateSource Source) : UIMessageBase<ContactListMembershipStateChanged>;

/// <summary>Updates a CardDAV address-book projection while its request executes.</summary>
public sealed record ContactAddressBookStateChanged(
    ContactAddressBook AddressBook,
    OptimisticEntityChange Change,
    EntityUpdateSource Source) : UIMessageBase<ContactAddressBookStateChanged>;
