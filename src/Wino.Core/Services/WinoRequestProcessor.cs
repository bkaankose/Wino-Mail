using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Tasks;
using Wino.Services.Interfaces;

namespace Wino.Core.Services;

/// <summary>
/// Intermediary processor for converting a user action to executable Wino requests.
/// Primarily responsible for batching requests by AccountId and FolderId.
/// </summary>
public class WinoRequestProcessor : IWinoRequestProcessor
{
    private readonly IFolderService _folderService;
    private readonly IPreferencesService _preferencesService;
    private readonly IMailService _mailService;
    private readonly IContactService _contactService;
    private readonly IMailProviderRegistry _providers;

    /// <summary>
    /// Set of rules that defines which action should be executed if user wants to toggle an action.
    /// </summary>
    private readonly List<ToggleRequestRule> _toggleRequestRules =
    [
        new ToggleRequestRule(MailOperation.MarkAsRead, MailOperation.MarkAsUnread, new System.Func<MailCopy, bool>((item) => item.IsRead)),
        new ToggleRequestRule(MailOperation.MarkAsUnread, MailOperation.MarkAsRead, new System.Func<MailCopy, bool>((item) => !item.IsRead)),
        new ToggleRequestRule(MailOperation.SetFlag, MailOperation.ClearFlag, new System.Func<MailCopy, bool>((item) => item.IsFlagged)),
        new ToggleRequestRule(MailOperation.ClearFlag, MailOperation.SetFlag, new System.Func<MailCopy, bool>((item) => !item.IsFlagged)),
    ];

    public WinoRequestProcessor(IFolderService folderService,
                                IPreferencesService preferencesService,
                                IMailService mailService,
                                IContactService contactService,
                                IMailProviderRegistry providers)
    {
        _providers = providers;
        _folderService = folderService;
        _preferencesService = preferencesService;
        _mailService = mailService;
        _contactService = contactService;
    }

    public async Task<IContactActionRequest> PrepareContactRequestAsync(ContactOperationPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Contact);

        if (request.Contact.MailAccountId == Guid.Empty)
            throw new ArgumentException("A contact request requires an account.", nameof(request));

        if (request.Contact.AddressBookId == Guid.Empty)
            throw new ArgumentException("A contact request requires an address book.", nameof(request));

        var addressBookId = request.Contact.AddressBookId;
        var books = await _contactService.GetAddressBooksAsync(request.Contact.MailAccountId).ConfigureAwait(false);
        if (books.FirstOrDefault(book => book.Id == addressBookId)?.IsReadOnly == true)
            throw new InvalidOperationException(Translator.ContactsPage_ReadOnlyAddressBookMessage);

        return new ContactActionRequest(
            request.Contact,
            request.Operation,
            request.OriginalContact,
            request.Photo);
    }

    public Task<ITaskActionRequest> PrepareTaskRequestAsync(TaskOperationPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AccountId == Guid.Empty)
            throw new ArgumentException("A task request requires an account.", nameof(request));

        if (request.List is null && request.Task is null && request.Step is null && request.Group is null)
            throw new ArgumentException("A task request requires a group, list, task, or step snapshot.", nameof(request));

        ITaskActionRequest prepared = new TaskActionRequest(
            request.AccountId,
            request.Operation,
            request.List,
            request.Task,
            request.Step,
            request.OriginalList,
            request.OriginalTask,
            request.OriginalStep,
            request.Group,
            request.OriginalGroup);

        return Task.FromResult(prepared);
    }

    public async Task<List<IMailActionRequest>> PrepareRequestsAsync(MailOperationPreperationRequest preperationRequest)
    {
        var action = preperationRequest.Action;
        var moveTargetStructure = preperationRequest.MoveTargetFolder;
        var mailItems = preperationRequest.MailItems?.Where(item => item != null).ToList() ?? [];

        if (mailItems.Count == 0)
            return [];

        // The caller confirms a protected permanent delete and picks a move destination before it gets here.
        // Drafts are always hard deleted without any protection.

        if (!preperationRequest.IgnoreHardDeleteProtection && action == MailOperation.HardDelete && _preferencesService.IsHardDeleteProtectionEnabled)
            throw new MailOperationDecisionRequiredException(MailOperationDecision.ConfirmPermanentDelete);

        if (action == MailOperation.Move && moveTargetStructure == null)
        {
            // One destination picker cannot serve mails from several accounts.
            if (mailItems.Select(a => a.AssignedAccount.Id).Distinct().Count() > 1)
                throw new InvalidMoveTargetException(InvalidMoveTargetReason.MultipleAccounts);

            throw new MailOperationDecisionRequiredException(MailOperationDecision.PickMoveTarget);
        }

        if (action == MailOperation.Move)
        {
            ValidateMoveTarget(mailItems, moveTargetStructure);
        }

        var requests = new List<IMailActionRequest>();

        // TODO: Fix: Collection was modified; enumeration operation may not execute
        foreach (var item in mailItems)
        {
            var singleRequest = await GetSingleRequestAsync(item, action, moveTargetStructure, preperationRequest.ToggleExecution);

            if (singleRequest == null) continue;

            requests.Add(singleRequest);
        }

        return PrependMarkAsReadForTrashMoves(requests);
    }

    private List<IMailActionRequest> PrependMarkAsReadForTrashMoves(List<IMailActionRequest> requests)
    {
        if (!_preferencesService.IsMarkAsReadOnMoveToTrashEnabled)
            return requests;

        // IMAP and EWS identify the message by its source folder, so the read flag must reach the
        // server before the move. All marks go first to keep each operation in a single batch.
        var markAsReadRequests = requests
            .OfType<MoveRequest>()
            .Where(request => request.ToFolder?.SpecialFolderType == SpecialFolderType.Deleted && !request.Item.IsRead)
            .Select(request => (IMailActionRequest)new MarkReadRequest(request.Item, true))
            .ToList();

        if (markAsReadRequests.Count == 0)
            return requests;

        markAsReadRequests.AddRange(requests);
        return markAsReadRequests;
    }

    private static void ValidateMoveTarget(
        IReadOnlyCollection<MailCopy> mailItems,
        IMailItemFolder moveTargetFolder)
    {
        if (moveTargetFolder == null || !moveTargetFolder.IsMoveTarget)
            throw new InvalidMoveTargetException(InvalidMoveTargetReason.NonMoveTarget);

        var hasInvalidSource = mailItems.Any(mailItem =>
            mailItem.AssignedAccount == null
            || mailItem.AssignedFolder == null
            || mailItem.AssignedAccount.Id != moveTargetFolder.MailAccountId
            || mailItem.AssignedFolder.Id == moveTargetFolder.Id);

        if (hasInvalidSource)
            throw new InvalidMoveTargetException(InvalidMoveTargetReason.NonMoveTarget);
    }

    private async Task<IMailActionRequest> GetSingleRequestAsync(MailCopy mailItem, MailOperation action, IMailItemFolder moveTargetStructure, bool shouldToggleActions)
    {
        if (mailItem.AssignedAccount == null) throw new ArgumentException(Translator.Exception_NullAssignedAccount);
        if (mailItem.AssignedFolder == null) throw new ArgumentException(Translator.Exception_NullAssignedFolder);

        var traits = _providers.GetTraits(mailItem.AssignedAccount.ProviderType);

        // Rule: Soft deletes from Trash folder must perform Hard Delete.
        if (action == MailOperation.SoftDelete && mailItem.AssignedFolder.SpecialFolderType == SpecialFolderType.Deleted)
            action = MailOperation.HardDelete;

        // Rule: SoftDelete draft items must be performed as hard delete.
        if (action == MailOperation.SoftDelete && mailItem.IsDraft)
            action = MailOperation.HardDelete;

        // A provider response may map the draft while the UI still holds the earlier local
        // snapshot. Resolve the discard under the same lifecycle lock as draft mapping.
        if (((action == MailOperation.SoftDelete || action == MailOperation.HardDelete) && mailItem.IsLocalDraft)
            || action == MailOperation.DiscardLocalDraft)
        {
            var mappedDraft = await _mailService
                .DiscardLocalDraftAsync(mailItem.AssignedAccount.Id, mailItem.UniqueId)
                .ConfigureAwait(false);
            return mappedDraft == null ? null : new DeleteRequest(mappedDraft);
        }

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
                throw new InvalidMoveTargetException(InvalidMoveTargetReason.NonMoveTarget);

            var pickedFolderItem = await _folderService.GetFolderAsync(moveTargetStructure.Id)
                ?? throw new InvalidMoveTargetException(InvalidMoveTargetReason.NonMoveTarget);

            ValidateMoveTarget([mailItem], pickedFolderItem);

            return new MoveRequest(mailItem, mailItem.AssignedFolder, pickedFolderItem);
        }
        else if (action == MailOperation.Archive)
        {
            MailItemFolder archiveFolder = null;

            if (traits.ArchiveRequiresArchiveFolder)
            {
                archiveFolder = await _folderService.GetSpecialFolderByAccountIdAsync(mailItem.AssignedAccount.Id, SpecialFolderType.Archive)
                ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Archive, mailItem.AssignedAccount.Id);
            }

            return new ArchiveRequest(true, mailItem, mailItem.AssignedFolder, archiveFolder);
        }
        else if (action == MailOperation.MarkAsNotJunk)
        {
            var inboxFolder = await _folderService.GetSpecialFolderByAccountIdAsync(mailItem.AssignedAccount.Id, SpecialFolderType.Inbox)
                ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Inbox, mailItem.AssignedAccount.Id);

            if (traits.MovesJunkAsFolderMove)
                return new MoveRequest(mailItem, mailItem.AssignedFolder, inboxFolder);

            return new ChangeJunkStateRequest(false, mailItem, mailItem.AssignedFolder, inboxFolder);
        }
        else if (action == MailOperation.UnArchive)
        {
            var inboxFolder = await _folderService.GetSpecialFolderByAccountIdAsync(mailItem.AssignedAccount.Id, SpecialFolderType.Inbox)
                ?? throw new UnavailableSpecialFolderException(SpecialFolderType.Inbox, mailItem.AssignedAccount.Id);

            return new ArchiveRequest(false, mailItem, mailItem.AssignedFolder, inboxFolder);
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

            if (traits.MovesJunkAsFolderMove)
                return new MoveRequest(mailItem, mailItem.AssignedFolder, junkFolder);

            return new ChangeJunkStateRequest(true, mailItem, mailItem.AssignedFolder, junkFolder);
        }
        else if (action is MailOperation.MoveToFocused or MailOperation.MoveToOther
                 or MailOperation.AlwaysMoveToFocused or MailOperation.AlwaysMoveToOther)
        {
            if (!traits.SupportsFocusedInbox)
                throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedAction, action));

            var moveToFocused = action is MailOperation.MoveToFocused or MailOperation.AlwaysMoveToFocused;

            if (action is MailOperation.MoveToFocused or MailOperation.MoveToOther)
                return new MoveToFocusedRequest(mailItem, moveToFocused);

            return new AlwaysMoveToRequest(mailItem, moveToFocused);
        }
        else
            throw new NotSupportedException(string.Format(Translator.Exception_UnsupportedAction, action));
    }

    public async Task<IFolderActionRequest> PrepareFolderRequestAsync(FolderOperationPreperationRequest request)
    {
        if (request == null || request.Folder == null) return default;

        IFolderActionRequest change = null;

        var folder = request.Folder;
        var operation = request.Action;

        // A container only groups a shared tree; it has no mail and cannot be changed on the server.
        if (folder.IsContainer)
            return default;

        switch (request.Action)
        {
            case FolderOperation.Pin:
            case FolderOperation.Unpin:
                await _folderService.ChangeStickyStatusAsync(folder.Id, operation == FolderOperation.Pin);
                break;

            case FolderOperation.Rename:
                var newFolderName = request.NewFolderName;

                if (!string.IsNullOrEmpty(newFolderName))
                {
                    change = new RenameFolderRequest(folder, folder.FolderName, newFolderName);
                }

                break;
            case FolderOperation.Empty:
                var mailsToDelete = await _mailService.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);

                change = new EmptyFolderRequest(folder, mailsToDelete);

                break;
            case FolderOperation.MarkAllAsRead:

                var unreadItems = await _mailService.GetUnreadMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);

                if (unreadItems.Any())
                    change = new MarkFolderAsReadRequest(folder, unreadItems);

                break;
            case FolderOperation.Delete:
                change = new DeleteFolderRequest(folder);

                break;
            case FolderOperation.CreateSubFolder:
                var subFolderName = request.NewFolderName;

                if (!string.IsNullOrWhiteSpace(subFolderName))
                {
                    change = new CreateSubFolderRequest(folder, subFolderName.Trim());
                }

                break;
        }

        return change;
    }
}
