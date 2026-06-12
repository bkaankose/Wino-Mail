using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Encapsulates the options for preparing requests to execute mail operations for mail items like Move, Delete, MarkAsRead, etc.
/// </summary>
/// <param name="Action"> Action to execute. </param>
/// <param name="MailItems"> Mail copies execute the action on. </param>
/// <param name="ToggleExecution"> Whether the operation can be reverted if needed.
/// eg. MarkAsRead on already read item will set the action to MarkAsUnread.
/// This is used in hover actions for example. </param>
/// <param name="IgnoreHardDeleteProtection"> Whether hard delete protection should be ignored.
/// Discard draft requests for example should ignore hard delete protection. </param>
/// <param name="MoveTargetFolder"> Moving folder for the Move operation.
/// If null and the action is Move, the user will be prompted to select a folder. </param>
public record MailOperationPreperationRequest
{
    [JsonConstructor]
    public MailOperationPreperationRequest(MailOperation action,
                                           IEnumerable<MailCopy> mailItems,
                                           bool toggleExecution,
                                           bool ignoreHardDeleteProtection,
                                           MailItemFolder moveTargetFolder)
    {
        Action = action;
        MailItems = mailItems ?? throw new ArgumentNullException(nameof(mailItems));
        ToggleExecution = toggleExecution;
        IgnoreHardDeleteProtection = ignoreHardDeleteProtection;
        MoveTargetFolder = moveTargetFolder;
    }

    public MailOperationPreperationRequest(MailOperation action,
                                           IEnumerable<MailCopy> mailItems,
                                           bool toggleExecution = false,
                                           MailItemFolder moveTargetFolder = null,
                                           bool ignoreHardDeleteProtection = false) : this(action, mailItems ?? throw new ArgumentNullException(nameof(mailItems)), toggleExecution, ignoreHardDeleteProtection, moveTargetFolder)
    {
    }

    public MailOperationPreperationRequest(MailOperation action,
                                           MailCopy singleMailItem,
                                           bool toggleExecution = false,
                                           MailItemFolder moveTargetFolder = null,
                                           bool ignoreHardDeleteProtection = false) : this(action, new List<MailCopy>() { singleMailItem }, toggleExecution, ignoreHardDeleteProtection, moveTargetFolder)
    {
    }

    public MailOperation Action { get; init; }
    public IEnumerable<MailCopy> MailItems { get; init; }
    public bool ToggleExecution { get; init; }
    public bool IgnoreHardDeleteProtection { get; init; }
    public MailItemFolder MoveTargetFolder { get; init; }
}
