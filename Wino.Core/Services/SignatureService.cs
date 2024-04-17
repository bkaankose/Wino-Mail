using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services
{
    public class SignatureService : BaseDatabaseService, ISignatureService
    {
        public SignatureService(IDatabaseService databaseService) : base(databaseService) { }

        public async Task<AccountSignature> CreateDefaultSignatureAsync(Guid accountId)
        {
            var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

            var defaultSignature = GetDefaultSignature();

            await Connection.InsertAsync(defaultSignature);

            account.SignatureId = defaultSignature.Id;

            await Connection.UpdateAsync(account);

            return defaultSignature;
        }

        public async Task DeleteAccountSignatureAssignment(Guid accountId)
        {
            var existingSignature = await GetAccountSignatureAsync(accountId);

            if (existingSignature != null)
            {
                await Connection.DeleteAsync(existingSignature);

                var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

                account.SignatureId = null;

                await Connection.UpdateAsync(account);
            }
        }

        public async Task<AccountSignature> GetAccountSignatureAsync(Guid accountId)
        {
            var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

            if (account?.SignatureId == null)
                return null;

            return await Connection.Table<AccountSignature>().FirstOrDefaultAsync(a => a.Id == account.SignatureId);
        }

        public async Task<AccountSignature> UpdateAccountSignatureAsync(Guid accountId, string htmlBody)
        {
            var signature = await GetAccountSignatureAsync(accountId);
            var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

            if (signature == null)
            {
                signature = new AccountSignature()
                {
                    Id = Guid.NewGuid(),
                    HtmlBody = htmlBody
                };

                await Connection.InsertAsync(signature);
            }
            else
            {
                signature.HtmlBody = htmlBody;

                await Connection.UpdateAsync(signature);
            }

            account.SignatureId = signature.Id;

            await Connection.UpdateAsync(account);

            return signature;
        }

        private AccountSignature GetDefaultSignature()
        {
            return new AccountSignature()
            {
                Id = Guid.NewGuid(),
                HtmlBody = @"<p>Sent from <a href=""https://github.com/bkaankose/Wino-Mail/"">Wino Mail</a> for Windows</p>"
            };
        }
    }
}
