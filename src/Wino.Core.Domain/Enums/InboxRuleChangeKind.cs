namespace Wino.Core.Domain.Enums;

/// <summary>A pending change to send to the server when writing inbox rules.</summary>
public enum InboxRuleChangeKind
{
    Create,
    Update,
    Delete
}
