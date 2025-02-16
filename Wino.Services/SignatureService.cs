using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class SignatureService(IDatabaseService databaseService) : BaseDatabaseService(databaseService), ISignatureService
{
    public async Task<AccountSignature> GetSignatureAsync(Guid signatureId)
    {
        return await Connection.Table<AccountSignature>().FirstAsync(s => s.Id == signatureId);
    }

    public async Task<List<AccountSignature>> GetSignaturesAsync(Guid accountId)
    {
        return await Connection.Table<AccountSignature>().Where(s => s.MailAccountId == accountId).ToListAsync();
    }

    public async Task<AccountSignature> CreateSignatureAsync(AccountSignature signature)
    {
        await Connection.InsertAsync(signature);

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

        await Connection.InsertAsync(defaultSignature);

        return defaultSignature;
    }

    public async Task<AccountSignature> UpdateSignatureAsync(AccountSignature signature)
    {
        await Connection.UpdateAsync(signature);

        return signature;
    }

    public async Task<AccountSignature> DeleteSignatureAsync(AccountSignature signature)
    {
        await Connection.DeleteAsync(signature);

        return signature;
    }
}
