using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class EmailTemplateService(IDatabaseService databaseService) : BaseDatabaseService(databaseService), IEmailTemplateService
{
    public Task<List<EmailTemplate>> GetEmailTemplatesAsync()
    {
        return Connection.Table<EmailTemplate>()
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public Task<EmailTemplate> GetEmailTemplateAsync(Guid templateId)
    {
        return Connection.Table<EmailTemplate>()
            .FirstOrDefaultAsync(t => t.Id == templateId);
    }

    public async Task<EmailTemplate> CreateEmailTemplateAsync(EmailTemplate template)
    {
        await Connection.InsertAsync(template, typeof(EmailTemplate)).ConfigureAwait(false);
        return template;
    }

    public async Task<EmailTemplate> UpdateEmailTemplateAsync(EmailTemplate template)
    {
        await Connection.UpdateAsync(template, typeof(EmailTemplate)).ConfigureAwait(false);
        return template;
    }

    public async Task<EmailTemplate> DeleteEmailTemplateAsync(EmailTemplate template)
    {
        await Connection.DeleteAsync<EmailTemplate>(template.Id).ConfigureAwait(false);
        return template;
    }
}
