using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Mail;

public record MailCategoryAssignmentRequest(
    MailCopy Item,
    Guid MailCategoryId,
    string CategoryName,
    IReadOnlyList<string> CategoryNames,
    bool IsAssigned) : MailRequestBase(Item), ICustomFolderSynchronizationRequest
{
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.UpdateCategories;
    public List<Guid> SynchronizationFolderIds => [Item.FolderId];
    public bool ExcludeMustHaveFolders => true;
}

public class BatchMailCategoryAssignmentRequest : BatchCollection<MailCategoryAssignmentRequest>
{
    public BatchMailCategoryAssignmentRequest(IEnumerable<MailCategoryAssignmentRequest> collection) : base(collection)
    {
    }
}
