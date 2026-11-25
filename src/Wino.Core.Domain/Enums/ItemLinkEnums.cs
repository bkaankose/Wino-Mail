namespace Wino.Core.Domain.Enums;

public enum ItemLinkKind
{
    Mail = 0,
    Event = 1,
    Task = 2,
    Contact = 3
}

public enum ItemLinkRelation
{
    CreatedFrom = 0,
    FollowUp = 1,
    TimeBlock = 2,
    Related = 3,
    CopyOf = 4
}
