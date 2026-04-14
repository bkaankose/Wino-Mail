using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Category;

public record MailCategoryCreateRequest(MailCategory Category) : CategoryRequestBase(Category.MailAccountId)
{
    public override CategorySynchronizerOperation Operation => CategorySynchronizerOperation.CreateCategory;
}
