using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces
{
    public interface IContactService
    {
        Task<List<AccountContact>> GetAddressInformationAsync(string queryText);
        Task<AccountContact> GetAddressInformationByAddressAsync(string address);
        Task SaveAddressInformationAsync(MimeMessage message);
    }
}
