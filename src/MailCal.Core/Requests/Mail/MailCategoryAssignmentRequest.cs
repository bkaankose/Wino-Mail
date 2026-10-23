using System;
using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Requests;

namespace MailCal.Core.Requests.Mail;

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
