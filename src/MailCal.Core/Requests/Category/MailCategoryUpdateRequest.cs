using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;

namespace MailCal.Core.Requests.Category;

public record MailCategoryUpdateRequest(
    MailCategory Category,
    string PreviousName,
    string PreviousRemoteId,
    IReadOnlyList<MailCategoryMessageUpdateTarget> AffectedMessages = null) : CategoryRequestBase(Category.MailAccountId)
{
    public override CategorySynchronizerOperation Operation => CategorySynchronizerOperation.UpdateCategory;
}
