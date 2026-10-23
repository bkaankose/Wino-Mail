using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;

namespace MailCal.Core.Requests.Category;

public record MailCategoryCreateRequest(MailCategory Category) : CategoryRequestBase(Category.MailAccountId)
{
    public override CategorySynchronizerOperation Operation => CategorySynchronizerOperation.CreateCategory;
}
