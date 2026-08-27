namespace Wino.Core.Domain.Enums;

public enum ContactSourceKind { Local = 0, Gmail = 1, Outlook = 2, CardDav = 3 }
public enum ContactPendingMutation { None = 0, Create = 1, Update = 2, Delete = 3, SetPhoto = 4, DeletePhoto = 5 }
public enum ContactPhoneKind { Home = 0, Work = 1, Mobile = 2 }
public enum ContactPostalAddressKind { Home = 0, Business = 1, Other = 2 }
public enum ContactRelationKind { Manager = 0, Assistant = 1, Spouse = 2, Child = 3 }
public enum ContactSynchronizationType { Full = 0, Delta = 1, ExecuteRequests = 2 }
public enum ContactSynchronizerOperation
{
    Create = 0,
    Update = 1,
    Delete = 2,
    SetPhoto = 3,
    DeletePhoto = 4,
    CreateAddressBook = 5,
    RenameAddressBook = 6,
    DeleteAddressBook = 7
}
