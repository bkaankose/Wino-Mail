namespace Wino.Core.Domain.Enums;

public enum InvalidMoveTargetReason
{
    NonMoveTarget, // This folder does not allow moving mails.
    MultipleAccounts // Multiple mails from different accounts cannot be moved.
}
