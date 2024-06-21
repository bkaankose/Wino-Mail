using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Requests;

namespace Wino.Core.Services
{
    /// <summary>
    /// Intermediary processor for converting a user action to executable Wino requests.
    /// Primarily responsible for batching requests by AccountId and FolderId.
    /// </summary>
    public class WinoRequestProcessor : BaseDatabaseService, IWinoRequestProcessor
    {
        private readonly IFolderService _folderService;
        private readonly IKeyPressService _keyPressService;
        private readonly IPreferencesService _preferencesService;
        private readonly IAccountService _accountService;
        private readonly IDialogService _dialogService;
        private readonly IMailService _mailService;

        /// <summary>
        /// Set of rules that defines which action should be executed if user wants to toggle an action.
        /// </summary>
        private readonly List<ToggleRequestRule> _toggleRequestRules =
        [
            new ToggleRequestRule(MailOperation.MarkAsRead, MailOperation.MarkAsUnread, new System.Func<IMailItem, bool>((item) => item.IsRead)),
            new ToggleRequestRule(MailOperation.MarkAsUnread, MailOperation.MarkAsRead, new System.Func<IMailItem, bool>((item) => !item.IsRead)),
            new ToggleRequestRule(MailOperation.SetFlag, MailOperation.ClearFlag, new System.Func<IMailItem, bool>((item) => item.IsFlagged)),
            new ToggleRequestRule(MailOperation.ClearFlag, MailOperation.SetFlag, new System.Func<IMailItem, bool>((item) => !item.IsFlagged)),
        ];

        public WinoRequestProcessor(IDatabaseService databaseService,
                                    IFolderService folderService,
                                    IKeyPressService keyPressService,
                                    IPreferencesService preferencesService,
                                    IAccountService accountService,
                                    IDialogService dialogService,
                                    IMailService mailService) : base(databaseService)
        {
            _folderService = folderService;
            _keyPressService = keyPressService;
            _preferencesService = preferencesService;
            _accountService = accountService;
            _dialogService = dialogService;
            _mailService = mailService;
        }

        public async Task<List<IRequest>> PrepareRequestsAsync(MailOperationPreperationRequest preperationRequest)
        {
            var action = preperationRequest.Action;
            var moveTargetStructure = preperationRequest.MoveTargetFolder;

            // Ask confirmation for permanent delete operation.
            // Drafts are always hard deleted without any protection.

            if (!preperationRequest.IgnoreHardDeleteProtection && ((action == MailOperation.SoftDelete && _keyPressService.IsShiftKeyPressed()) || action == MailOperation.HardDelete))
            {
                if (_preferencesService.IsHardDeleteProtectionEnabled)
                {
                    var shouldDelete = await _dialogService.ShowHardDeleteConfirmationAsync();

                    if (!shouldDelete) return default;
                }

                action = MailOperation.HardDelete;
            }

            // Make sure there is a move target folder if action is move.
            // Let user pick a folder to move from the dialog.

            if (action == MailOperation.Move && moveTargetStructure == null)
            {
                // TODO: Handle multiple accounts for move operation.
                // What happens if we move 2 different mails from 2 different accounts?

                var accountId = preperationRequest.MailItems.FirstOrDefault().AssignedAccount.Id;

                moveTargetStructure = await _dialogService.PickFolderAsync(accountId, PickFolderReason.Move, _folderService);

                if (moveTargetStructure == null)
                    return default;
            }

            var requests = new List<IRequest>();

            foreach (var item in preperationRequest.MailItems)
            {
                var singleRequest = await GetSingleRequestAsync(item, action, moveTargetStructure, preperationRequest.ToggleExecution);

                if (singleRequest == null) continue;

                requests.Add(singleRequest);
            }

            return requests;
        }

        private async Task<IRequest> GetSingleRequestAsync(MailCopy mailItem, MailOperation action, IMailItemFolder moveTargetStructure, bool shouldToggleActions)
        {
            if (mailItem.AssignedAccount == null) throw new ArgumentException(Translator.Exception_NullAssignedAccount);
            if (mailItem.AssignedFolder == null) throw new ArgumentException(Translator.Exception_NullAssignedFolder);

            // Rule: Soft deletes from Trash folder must perform Hard Delete.
            if (action == MailOperation.SoftDelete && mailItem.AssignedFolder.SpecialFolderType == SpecialFolderType.Deleted)
                action = MailOperation.HardDelete;

            // Rule: SoftDelete draft items must be performed as hard delete.
            if (action == MailOperation.SoftDelete && mailItem.IsDraft)
                action = MailOperation.HardDelete;

            // Rule: Soft/Hard deletes on local drafts are always discard local draft.
            if ((action == MailOperation.SoftDelete || action == MailOperation.HardDelete) && mailItem.IsLocalDraft)
                action = MailOperation.DiscardLocalDraft;

            // Rule: Toggle actions must be reverted if ToggleExecution is passed true.
            if (shouldToggleActions)
            {
                var toggleRule = _toggleRequestRules.Find(a => a.SourceAction == action);

                if (toggleRule != null && toggleRule.Condition(mailItem))
                {
                    action = toggleRule.TargetAction;
                }
            }

            if (action == MailOperation.MarkAsRead)
                return new MarkReadRequest(mailItem, true);
            else if (action == MailOperation.MarkAsUnread)
                return new MarkReadRequest(mailItem, false);
            else if (action == MailOperation.SetFlag)
                return new ChangeFlagRequest(mailItem, true);
            else if (action == MailOperation.ClearFlag)
                return new ChangeFlagRequest(mailItem, false);
            else if (action == MailOperation.HardDelete)
                return new DeleteRequest(mailItem);
            else if (action == MailOperation.Move)
            {
                if (moveTargetStructure == null)
                    throw new InvalidMoveTargetException();

                // TODO
                // Rule: You can't move items to non-move target folders;
                // Rule: You can't move items from a folder to itself.

                //if (!moveTargetStructure.IsMoveTarget || moveTargetStructure.FolderId == mailItem.AssignedFolder.Id)
                //    throw new InvalidMoveTargetException();

                var pickedFolderItem = await _folderService.GetFolderAsync(moveTargetStructure.Id);

                return new MoveRequest(mailItem, mailItem.AssignedFolder, pickedFolderItem);
            }
            else if (action == MailOperation.Archive)
            {
                // Validate archive folder exists.

                var archiveFolder = await _folderService.GetSpecialFolderByAccountIdAsync(mailItem.AssignedAccount.Id, SpecialFolderType.Archive)
                    ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Archive, mailItem.AssignedAccount.Id);

                return new MoveRequest(mailItem, mailItem.AssignedFolder, archiveFolder);
            }
            else if (action == MailOperation.UnArchive || action == MailOperation.MarkAsNotJunk)
            {
                var inboxFolder = await _folderService.GetSpecialFolderByAccountIdAsync(mailItem.AssignedAccount.Id, SpecialFolderType.Inbox)
                    ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Inbox, mailItem.AssignedAccount.Id);

                return new MoveRequest(mailItem, mailItem.AssignedFolder, inboxFolder);
            }
            else if (action == MailOperation.SoftDelete)
            {
                var trashFolder = await _folderService.GetSpecialFolderByAccountIdAsync(mailItem.AssignedAccount.Id, SpecialFolderType.Deleted)
                    ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Deleted, mailItem.AssignedAccount.Id);

                return new MoveRequest(mailItem, mailItem.AssignedFolder, trashFolder);
            }
            else if (action == MailOperation.MoveToJunk)
            {
                var junkFolder = await _folderService.GetSpecialFolderByAccountIdAsync(mailItem.AssignedAccount.Id, SpecialFolderType.Junk)
                    ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Junk, mailItem.AssignedAccount.Id);

                return new MoveRequest(mailItem, mailItem.AssignedFolder, junkFolder);
            }
            else if (action == MailOperation.AlwaysMoveToFocused || action == MailOperation.AlwaysMoveToOther)
                return new AlwaysMoveToRequest(mailItem, action == MailOperation.AlwaysMoveToFocused);
            else if (action == MailOperation.DiscardLocalDraft)
                await _mailService.DeleteMailAsync(mailItem.AssignedAccount.Id, mailItem.Id);
            else
                throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedAction, action));

            return null;
        }

        public async Task<IRequest> PrepareFolderRequestAsync(FolderOperation operation, IMailItemFolder mailItemFolder)
        {
            if (mailItemFolder == null) return default;

            var accountId = mailItemFolder.MailAccountId;

            IRequest change = null;

            switch (operation)
            {
                case FolderOperation.Pin:
                case FolderOperation.Unpin:
                    await _folderService.ChangeStickyStatusAsync(mailItemFolder.Id, operation == FolderOperation.Pin);
                    break;
                    //case FolderOperation.MarkAllAsRead:
                    //    // Get all mails in the folder.

                    //    var mailItems = await _folderService.GetAllUnreadItemsByFolderIdAsync(accountId, folderStructure.RemoteFolderId).ConfigureAwait(false);

                    //    if (mailItems.Any())
                    //        change = new FolderMarkAsReadRequest(accountId, mailItems.Select(a => a.Id).Distinct(), folderStructure.RemoteFolderId, folderStructure.FolderId);

                    //    break;
                    //case FolderOperation.Empty:
                    //    // Get all mails in the folder.

                    //    var mailsToDelete = await _folderService.GetMailByFolderIdAsync(folderStructure.FolderId).ConfigureAwait(false);

                    //    if (mailsToDelete.Any())
                    //        change = new FolderEmptyRequest(accountId, mailsToDelete.Select(a => a.Id).Distinct(), folderStructure.RemoteFolderId, folderStructure.FolderId);

                    //    break;
                    //case FolderOperation.Rename:
                    //    var newFolderName = await _dialogService.ShowRenameFolderDialogAsync(folderStructure.FolderName);

                    //    if (!string.IsNullOrEmpty(newFolderName))
                    //        change = new RenameFolderRequest(accountId, folderStructure.RemoteFolderId, folderStructure.FolderId, newFolderName, folderStructure.FolderName);

                    //    break;
                    //case FolderOperation.Delete:
                    //    var isConfirmed = await _dialogService.ShowConfirmationDialogAsync($"'{folderStructure.FolderName}' is going to be deleted. Do you want to continue?", "Are you sure?", "Yes delete.");

                    //    if (isConfirmed)
                    //        change = new DeleteFolderRequest(accountId, folderStructure.RemoteFolderId, folderStructure.FolderId);

                    //    break;
                    //default:
                    //    throw new NotImplementedException();
            }

            return change;
        }
    }
}
