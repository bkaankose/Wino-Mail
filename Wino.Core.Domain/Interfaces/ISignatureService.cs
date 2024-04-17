using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface ISignatureService
    {
        /// <summary>
        /// Returns the assigned account signature for the account.
        /// </summary>
        /// <param name="accountId"></param>
        /// <returns></returns>
        Task<AccountSignature> GetAccountSignatureAsync(Guid accountId);

        /// <summary>
        /// Creates the initial signature for new created accounts.
        /// </summary>
        /// <param name="accountId"></param>
        /// <returns></returns>
        Task<AccountSignature> CreateDefaultSignatureAsync(Guid accountId);

        /// <summary>
        /// Updates account's existing signature with the given HTML signature.
        /// </summary>
        Task<AccountSignature> UpdateAccountSignatureAsync(Guid accountId, string htmlBody);

        /// <summary>
        /// Disabled signature for the account and deletes existing signature.
        /// </summary>
        Task DeleteAccountSignatureAssignment(Guid accountId);
    }
}
