using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface IEmailTemplateService
{
    Task<List<EmailTemplate>> GetEmailTemplatesAsync();
    Task<EmailTemplate> GetEmailTemplateAsync(Guid templateId);
    Task<EmailTemplate> CreateEmailTemplateAsync(EmailTemplate template);
    Task<EmailTemplate> UpdateEmailTemplateAsync(EmailTemplate template);
    Task<EmailTemplate> DeleteEmailTemplateAsync(EmailTemplate template);
}
