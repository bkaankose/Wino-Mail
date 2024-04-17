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
    public class MailOperationPreperationRequest
    {
        public MailOperationPreperationRequest(MailOperation action,
                                               IEnumerable<MailCopy> mailItems,
                                               bool toggleExecution = false,
                                               IMailItemFolder moveTargetFolder = null,
                                               bool ignoreHardDeleteProtection = false)
        {
            Action = action;
            MailItems = mailItems ?? throw new ArgumentNullException(nameof(mailItems));
            ToggleExecution = toggleExecution;
            MoveTargetFolder = moveTargetFolder;
            IgnoreHardDeleteProtection = ignoreHardDeleteProtection;
        }

        public MailOperationPreperationRequest(MailOperation action,
                                               MailCopy singleMailItem,
                                               bool toggleExecution = false,
                                               IMailItemFolder moveTargetFolder = null,
                                               bool ignoreHardDeleteProtection = false)
        {
            Action = action;
            MailItems = new List<MailCopy>() { singleMailItem };
            ToggleExecution = toggleExecution;
            MoveTargetFolder = moveTargetFolder;
            IgnoreHardDeleteProtection = ignoreHardDeleteProtection;
        }

        /// <summary>
        /// Action to execute.
        /// </summary>
        public MailOperation Action { get; set; }

        /// <summary>
        /// Mail copies execute the action on.
        /// </summary>
        public IEnumerable<MailCopy> MailItems { get; set; }

        /// <summary>
        /// Whether the operation can be reverted if needed.
        /// eg. MarkAsRead on already read item will set the action to MarkAsUnread.
        /// This is used in hover actions for example.
        /// </summary>
        public bool ToggleExecution { get; set; }

        /// <summary>
        /// Whether hard delete protection should be ignored.
        /// Discard draft requests for example should ignore hard delete protection.
        /// </summary>
        public bool IgnoreHardDeleteProtection { get; set; }

        /// <summary>
        /// Moving folder for the Move operation.
        /// If null and the action is Move, the user will be prompted to select a folder.
        /// </summary>
        public IMailItemFolder MoveTargetFolder { get; }
    }
}
