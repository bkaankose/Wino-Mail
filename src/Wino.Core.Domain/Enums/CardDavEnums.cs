namespace Wino.Core.Domain.Enums;

public enum CardDavOutboxOperation
{
    CreateContact = 0,
    UpdateContact = 1,
    DeleteContact = 2,
    MoveContact = 3,
    CreateAddressBook = 4,
    RenameAddressBook = 5,
    DeleteAddressBook = 6
}

public enum CardDavOutboxState
{
    Pending = 0,
    Leased = 1,
    BlockedByConflict = 2,
    Completed = 3
}

public enum CardDavConflictKind
{
    ConcurrentUpdate = 0,
    LocalUpdateRemoteDelete = 1,
    LocalDeleteRemoteUpdate = 2,
    ResourceReplaced = 3,
    DuplicateUid = 4,
    UnsupportedPropertyChanged = 5
}

public enum CardDavConflictResolution
{
    Unresolved = 0,
    UseServer = 1,
    UseLocal = 2,
    KeepBoth = 3
}

public enum CardDavResourceStatus
{
    Active = 0,
    Quarantined = 1,
    Deleted = 2
}
