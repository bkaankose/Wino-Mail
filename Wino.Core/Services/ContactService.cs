using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using SqlKata;
using Wino.Core.Domain.Entities;
using Wino.Core.Extensions;

namespace Wino.Core.Services
{
    public interface IContactService
    {
        Task<List<AccountContact>> GetAddressInformationAsync(string queryText);
        Task<AccountContact> GetAddressInformationByAddressAsync(string address);
        Task SaveAddressInformationAsync(MimeMessage message);
    }

    public class ContactService : BaseDatabaseService, IContactService
    {
        public ContactService(IDatabaseService databaseService) : base(databaseService) { }

        public Task<List<AccountContact>> GetAddressInformationAsync(string queryText)
        {
            if (queryText == null || queryText.Length < 2)
                return Task.FromResult<List<AccountContact>>(null);

            var query = new Query(nameof(AccountContact));
            query.WhereContains("Address", queryText);
            query.OrWhereContains("Name", queryText);

            var rawLikeQuery = query.GetRawQuery();

            return Connection.QueryAsync<AccountContact>(rawLikeQuery);
        }

        public Task<AccountContact> GetAddressInformationByAddressAsync(string address)
            => Connection.Table<AccountContact>().Where(a => a.Address == address).FirstOrDefaultAsync();

        public async Task SaveAddressInformationAsync(MimeMessage message)
        {
            var recipients = message
                        .GetRecipients(true)
                        .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Address));

            var addressInformations = recipients.Select(a => new AccountContact() { Name = a.Name, Address = a.Address });

            foreach (var info in addressInformations)
            {
                var currentContact = await GetAddressInformationByAddressAsync(info.Address).ConfigureAwait(false);

                if (currentContact == null)
                {
                    await Connection.InsertAsync(info).ConfigureAwait(false);
                }
                await Connection.InsertOrReplaceAsync(info).ConfigureAwait(false);
            }
        }
    }
}
