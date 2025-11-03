using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class SignatureService(IDatabaseService databaseService) : BaseDatabaseService(databaseService), ISignatureService
{
    public async Task<AccountSignature> GetSignatureAsync(Guid signatureId)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.AccountSignatures.FirstAsync(s => s.Id == signatureId);
    }

    public async Task<List<AccountSignature>> GetSignaturesAsync(Guid accountId)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.AccountSignatures
            .Where(s => s.MailAccountId == accountId)
            .ToListAsync();
    }

    public async Task<AccountSignature> CreateSignatureAsync(AccountSignature signature)
    {
        using var context = ContextFactory.CreateDbContext();
        context.AccountSignatures.Add(signature);
        await context.SaveChangesAsync();

        return signature;
    }

    public async Task<AccountSignature> CreateDefaultSignatureAsync(Guid accountId)
    {
        var defaultSignature = new AccountSignature()
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            // TODO: Should be translated?
            Name = "Wino Default Signature",
            HtmlBody = @"<p>Sent from <a href=""https://github.com/bkaankose/Wino-Mail/"">Wino Mail</a> for Windows</p>"
        };

        using var context = ContextFactory.CreateDbContext();
        context.AccountSignatures.Add(defaultSignature);
        await context.SaveChangesAsync();

        return defaultSignature;
    }

    public async Task<AccountSignature> UpdateSignatureAsync(AccountSignature signature)
    {
        using var context = ContextFactory.CreateDbContext();
        context.AccountSignatures.Update(signature);
        await context.SaveChangesAsync();

        return signature;
    }

    public async Task<AccountSignature> DeleteSignatureAsync(AccountSignature signature)
    {
        using var context = ContextFactory.CreateDbContext();
        context.AccountSignatures.Remove(signature);
        await context.SaveChangesAsync();

        return signature;
    }
}
