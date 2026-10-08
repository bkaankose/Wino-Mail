namespace Wino.Core.Domain.Enums;

public enum MailFilterManagementType
{
    WinoLocal,
    Provider
}

public enum MailFilterMatchMode
{
    All,
    Any
}

public enum MailFilterConditionField
{
    FromAddress,
    FromName,
    Subject,
    PreviewText,
    HasAttachments,
    Importance
}

public enum MailFilterConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith
}

public enum MailFilterActionType
{
    MarkRead,
    MarkUnread,
    SetFlag,
    ClearFlag,
    Move,
    Archive,
    MoveToJunk,
    MarkAsNotJunk,
    SoftDelete,
    HardDelete
}

public enum MailFilterExecutionState
{
    Pending,
    Completed,
    Failed
}
