using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using SqlKata;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Extensions;

namespace Wino.Services;

public class ContactService : BaseDatabaseService, IContactService
{
    public ContactService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<AccountContact> CreateNewContactAsync(string address, string displayName)
    {
        var contact = new AccountContact() { Address = address, Name = displayName };

        await Connection.InsertAsync(contact).ConfigureAwait(false);

        return contact;
    }

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
        => Connection.Table<AccountContact>().FirstOrDefaultAsync(a => a.Address == address);

    public async Task SaveAddressInformationAsync(MimeMessage message)
    {
        var recipients = message
                    .GetRecipients(true)
                    .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Address));

        var addressInformations = recipients.Select(a => new AccountContact() { Name = a.Name, Address = a.Address });

        foreach (var info in addressInformations)
        {
            var currentContact = await GetAddressInformationByAddressAsync(info.Address).ConfigureAwait(false);

            try
            {
                if (currentContact == null)
                {
                    await Connection.InsertAsync(info).ConfigureAwait(false);
                }
                else if (!currentContact.IsRootContact) // Don't update root contacts. They belong to accounts.
                {
                    await Connection.InsertOrReplaceAsync(info).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to add contact information to the database.", ex);
            }
        }
    }
}
