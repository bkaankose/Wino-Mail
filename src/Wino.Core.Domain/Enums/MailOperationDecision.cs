namespace Wino.Core.Domain.Enums;

/// <summary>An answer a mail operation needs from the user before the backend can prepare its requests.</summary>
public enum MailOperationDecision
{
    /// <summary>The permanent delete is protected and was not confirmed.</summary>
    ConfirmPermanentDelete,

    /// <summary>A move has no destination folder.</summary>
    PickMoveTarget,

    /// <summary>The move goes into an account that needs the user's permission to receive mail from other accounts.</summary>
    AuthorizeMailImport
}
