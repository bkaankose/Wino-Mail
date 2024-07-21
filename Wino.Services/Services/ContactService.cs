using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using SqlKata;
using Wino.Domain.Entities;
using Wino.Domain.Interfaces;
using Wino.Services.Extensions;

namespace Wino.Services.Services
{
    public class ContactService : BaseDatabaseService, IContactService
    {
        public ContactService(IDatabaseService databaseService) : base(databaseService) { }

        public Task<List<AddressInformation>> GetAddressInformationAsync(string queryText)
        {
            if (queryText == null || queryText.Length < 2)
                return Task.FromResult<List<AddressInformation>>(null);

            var query = new Query(nameof(AddressInformation));
            query.WhereContains("Address", queryText);
            query.OrWhereContains("Name", queryText);

            var rawLikeQuery = query.GetRawQuery();

            return Connection.QueryAsync<AddressInformation>(rawLikeQuery);
        }

        public async Task<AddressInformation> GetAddressInformationByAddressAsync(string address)
        {
            return await Connection.Table<AddressInformation>().Where(a => a.Address == address).FirstOrDefaultAsync()
                ?? new AddressInformation() { Name = address, Address = address };
        }

        public async Task SaveAddressInformationAsync(MimeMessage message)
        {
            var recipients = message
                        .GetRecipients(true)
                        .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Address));

            var addressInformations = recipients.Select(a => new AddressInformation() { Name = a.Name, Address = a.Address });

            foreach (var info in addressInformations)
                await Connection.InsertOrReplaceAsync(info).ConfigureAwait(false);
        }
    }
}
