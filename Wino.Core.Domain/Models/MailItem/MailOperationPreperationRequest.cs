using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Models.MailItem
{
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
    public record MailOperationPreperationRequest(MailOperation Action, IEnumerable<MailCopy> MailItems, bool ToggleExecution, bool IgnoreHardDeleteProtection, IMailItemFolder MoveTargetFolder)
    {
        public MailOperationPreperationRequest(MailOperation action,
                                               IEnumerable<MailCopy> mailItems,
                                               bool toggleExecution = false,
                                               IMailItemFolder moveTargetFolder = null,
                                               bool ignoreHardDeleteProtection = false) : this(action, mailItems ?? throw new ArgumentNullException(nameof(mailItems)), toggleExecution, ignoreHardDeleteProtection, moveTargetFolder)
        {
        }

        public MailOperationPreperationRequest(MailOperation action,
                                               MailCopy singleMailItem,
                                               bool toggleExecution = false,
                                               IMailItemFolder moveTargetFolder = null,
                                               bool ignoreHardDeleteProtection = false) : this(action, new List<MailCopy>() { singleMailItem }, toggleExecution, ignoreHardDeleteProtection, moveTargetFolder)
        {
        }
    }
}
