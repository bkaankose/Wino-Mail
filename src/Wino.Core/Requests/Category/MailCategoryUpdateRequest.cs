using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Category;

public record MailCategoryUpdateRequest(
    MailCategory Category,
    string PreviousName,
    string PreviousRemoteId,
    IReadOnlyList<MailCategoryMessageUpdateTarget> AffectedMessages = null) : CategoryRequestBase(Category.MailAccountId)
{
    public override CategorySynchronizerOperation Operation => CategorySynchronizerOperation.UpdateCategory;
}
