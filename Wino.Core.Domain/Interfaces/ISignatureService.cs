using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces
{
    public interface ISignatureService
    {
        /// <summary>
        /// Get one signature by Id.
        /// </summary>
        /// <param name="signatureId">Signature Id.</param>
        Task<AccountSignature> GetSignatureAsync(Guid signatureId);

        /// <summary>
        /// Returns all signatures for specified account.
        /// </summary>
        /// <param name="accountId">Account id</param>
        Task<List<AccountSignature>> GetSignaturesAsync(Guid accountId);

        /// <summary>
        /// Creates a new signature for the account.
        /// </summary>
        /// <param name="signature">Signature that should be created. It should contain ID and account to which it belongs.</param>
        Task<AccountSignature> CreateSignatureAsync(AccountSignature signature);

        /// <summary>
        /// Creates a default Wino signature for the account.
        /// Needed only for initial account setup.
        /// </summary>
        /// <param name="accountId">Account Id.</param>
        Task<AccountSignature> CreateDefaultSignatureAsync(Guid accountId);

        /// <summary>
        /// Updates existing signature.
        /// </summary>
        /// <param name="signature">Signature that should be updated. It should contain ID and account to which it belongs.</param>
        Task<AccountSignature> UpdateSignatureAsync(AccountSignature signature);

        /// <summary>
        /// Deletes existing signature.
        /// </summary>
        /// <param name="signature">Signature that should be deleted.</param>
        Task<AccountSignature> DeleteSignatureAsync(AccountSignature signature);
    }
}
