using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

public class ContactSynchronizationOptions
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public ContactSynchronizationType Type { get; set; }
    public Guid? AddressBookId { get; set; }
}
