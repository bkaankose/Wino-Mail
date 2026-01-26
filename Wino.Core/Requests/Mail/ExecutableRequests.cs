using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Kiota.Abstractions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Mail;

/// <summary>
/// Mark read/unread request that supports the new executable request system.
/// </summary>
public record MarkReadRequestV2(MailCopy Item, bool IsRead) 
    : ExecutableMailRequestBase<RequestInformation>(Item), 
      ICustomFolderSynchronizationRequest
{
    public List<Guid> SynchronizationFolderIds => [Item.FolderId];
    public bool ExcludeMustHaveFolders => true;
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.MarkRead;

    public override Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        // For Outlook: Create RequestInformation for marking message as read/unread
        // This will be implemented differently for each synchronizer
        throw new NotImplementedException("PrepareNativeRequestAsync must be implemented by synchronizer-specific factory");
    }

    public override void ApplyUIChanges()
    {
        Item.IsRead = IsRead;
        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
    }

    public override void RevertUIChanges()
    {
        Item.IsRead = !IsRead;
        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
    }
}

/// <summary>
/// Delete request that supports the new executable request system.
/// </summary>
public record DeleteRequestV2(MailCopy MailItem) 
    : ExecutableMailRequestBase<RequestInformation>(MailItem),
      ICustomFolderSynchronizationRequest
{
    public List<Guid> SynchronizationFolderIds => [Item.FolderId];
    public bool ExcludeMustHaveFolders => false;
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Delete;

    public override Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        throw new NotImplementedException("PrepareNativeRequestAsync must be implemented by synchronizer-specific factory");
    }

    public override void ApplyUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
    }

    public override void RevertUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
    }
}

/// <summary>
/// Change flag request that supports the new executable request system.
/// </summary>
public record ChangeFlagRequestV2(MailCopy Item, bool IsFlagged) 
    : ExecutableMailRequestBase<RequestInformation>(Item),
      ICustomFolderSynchronizationRequest
{
    public List<Guid> SynchronizationFolderIds => [Item.FolderId];
    public bool ExcludeMustHaveFolders => true;
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.ChangeFlag;

    public override Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        throw new NotImplementedException("PrepareNativeRequestAsync must be implemented by synchronizer-specific factory");
    }

    public override void ApplyUIChanges()
    {
        Item.IsFlagged = IsFlagged;
        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
    }

    public override void RevertUIChanges()
    {
        Item.IsFlagged = !IsFlagged;
        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
    }
}

/// <summary>
/// Move request that supports the new executable request system.
/// </summary>
public record MoveRequestV2(MailCopy Item, MailItemFolder FromFolder, MailItemFolder ToFolder) 
    : ExecutableMailRequestBase<RequestInformation>(Item),
      ICustomFolderSynchronizationRequest
{
    public List<Guid> SynchronizationFolderIds => [FromFolder.Id, ToFolder.Id];
    public bool ExcludeMustHaveFolders => false;
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Move;

    private Guid _originalFolderId;

    public override Task<RequestInformation> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        // Store original folder ID for rollback
        _originalFolderId = Item.FolderId;
        throw new NotImplementedException("PrepareNativeRequestAsync must be implemented by synchronizer-specific factory");
    }

    public override void ApplyUIChanges()
    {
        // Remove from old folder
        WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));

        // Update folder assignment
        Item.FolderId = ToFolder.Id;
        Item.AssignedFolder = ToFolder;

        // Add to new folder
        WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
    }

    public override void RevertUIChanges()
    {
        // Remove from current folder
        WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));

        // Restore original folder
        Item.FolderId = _originalFolderId;
        Item.AssignedFolder = FromFolder;

        // Add back to original folder
        WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
    }
}
