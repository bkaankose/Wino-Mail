using MimeKit;
using Wino.Domain.Entities;

namespace Wino.Domain.Interfaces
{
    public interface IContactService
    {
        Task<List<AddressInformation>> GetAddressInformationAsync(string queryText);
        Task<AddressInformation> GetAddressInformationByAddressAsync(string address);
        Task SaveAddressInformationAsync(MimeMessage message);
    }
}
