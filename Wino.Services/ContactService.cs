using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Services.Extensions;

namespace Wino.Services;

public class ContactService : BaseDatabaseService, IContactService
{
    public ContactService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<AccountContact> CreateNewContactAsync(string address, string displayName)
    {
        var contact = new AccountContact() { Address = address, Name = displayName };

        await Connection.InsertAsync(contact, typeof(AccountContact)).ConfigureAwait(false);

        return contact;
    }

    public Task<List<AccountContact>> GetAddressInformationAsync(string queryText)
    {
        if (queryText == null || queryText.Length < 2)
            return Task.FromResult<List<AccountContact>>(null);

        const string query = "SELECT * FROM AccountContact WHERE Address LIKE ? OR Name LIKE ?";
        var pattern = $"%{queryText}%";
        return Connection.QueryAsync<AccountContact>(query, pattern, pattern);
    }

    public Task<AccountContact> GetAddressInformationByAddressAsync(string address)
        => Connection.Table<AccountContact>().FirstOrDefaultAsync(a => a.Address == address);

    public async Task SaveAddressInformationAsync(MimeMessage message)
    {
        if (message == null) return;

        var contacts = message
            .GetRecipients(true)
            .Where(a => !string.IsNullOrWhiteSpace(a.Address))
            .Select(a => new AccountContact
            {
                Name = string.IsNullOrWhiteSpace(a.Name) ? a.Address : a.Name,
                Address = a.Address
            });

        await SaveAddressInformationInternalAsync(contacts).ConfigureAwait(false);
    }

    public async Task SaveAddressInformationAsync(IEnumerable<AccountContact> contacts)
    {
        if (contacts == null) return;

        await SaveAddressInformationInternalAsync(contacts).ConfigureAwait(false);
    }

    private async Task SaveAddressInformationInternalAsync(IEnumerable<AccountContact> contacts)
    {
        var addressInformations = contacts
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Address))
            .Select(a => new AccountContact
            {
                Address = a.Address.Trim(),
                Name = string.IsNullOrWhiteSpace(a.Name) ? a.Address.Trim() : a.Name.Trim()
            })
            .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (addressInformations.Count == 0) return;

        try
        {
            // Batch-fetch all existing contacts in one query.
            var addresses = addressInformations.Select(a => a.Address).ToList();
            var placeholders = string.Join(",", addresses.Select((_, i) => "?"));
            var existingContacts = await Connection.QueryAsync<AccountContact>(
                $"SELECT * FROM AccountContact WHERE Address IN ({placeholders})",
                addresses.Cast<object>().ToArray()
            ).ConfigureAwait(false);

            var existingLookup = existingContacts.ToDictionary(c => c.Address, StringComparer.OrdinalIgnoreCase);

            var toInsert = new List<AccountContact>();
            var toUpdate = new List<AccountContact>();

            foreach (var info in addressInformations)
            {
                if (!existingLookup.TryGetValue(info.Address, out var existing))
                {
                    toInsert.Add(info);
                }
                else if (!existing.IsRootContact && !existing.IsOverridden)
                {
                    // Only update if the new name is more informative (not just the email address)
                    // and actually different from the current name.
                    if (info.Name != info.Address && existing.Name != info.Name)
                    {
                        existing.Name = info.Name;
                        toUpdate.Add(existing);
                    }
                }
            }

            if (toInsert.Count > 0 || toUpdate.Count > 0)
            {
                await Connection.RunInTransactionAsync(conn =>
                {
                    if (toInsert.Count > 0)
                        conn.InsertAll(toInsert, typeof(AccountContact));

                    foreach (var contact in toUpdate)
                        conn.Update(contact, typeof(AccountContact));
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to batch save contact information to the database.");
        }
    }

    public Task<List<AccountContact>> GetAllContactsAsync()
    {
        return Connection.Table<AccountContact>().ToListAsync();
    }

    public Task<List<AccountContact>> SearchContactsAsync(string searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return GetAllContactsAsync();

        const string query = "SELECT * FROM AccountContact WHERE Address LIKE ? OR Name LIKE ?";
        var pattern = $"%{searchQuery.Trim()}%";
        return Connection.QueryAsync<AccountContact>(query, pattern, pattern);
    }

    public async Task<PagedContactsResult> GetContactsPageAsync(int offset, int pageSize, string searchQuery = null, bool excludeRootContacts = false)
    {
        offset = Math.Max(0, offset);
        pageSize = Math.Max(1, pageSize);

        var whereClauses = new List<string>();
        var parameters = new List<object>();

        if (excludeRootContacts)
        {
            whereClauses.Add("IsRootContact = 0");
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var pattern = $"%{searchQuery.Trim()}%";
            whereClauses.Add("(Address LIKE ? OR Name LIKE ?)");
            parameters.Add(pattern);
            parameters.Add(pattern);
        }

        var whereSql = whereClauses.Count > 0
            ? $" WHERE {string.Join(" AND ", whereClauses)}"
            : string.Empty;

        var countQuery = $"SELECT COUNT(*) FROM AccountContact{whereSql}";
        var totalCount = await Connection.ExecuteScalarAsync<int>(countQuery, parameters.ToArray()).ConfigureAwait(false);

        var pageParameters = new List<object>(parameters)
        {
            pageSize,
            offset
        };

        var pageQuery = $"SELECT * FROM AccountContact{whereSql} ORDER BY COALESCE(Name, Address) COLLATE NOCASE, Address COLLATE NOCASE LIMIT ? OFFSET ?";
        var contacts = await Connection.QueryAsync<AccountContact>(pageQuery, pageParameters.ToArray()).ConfigureAwait(false);
        var hasMore = offset + contacts.Count < totalCount;

        return new PagedContactsResult(contacts, totalCount, hasMore, offset, pageSize);
    }

    public async Task<AccountContact> UpdateContactAsync(AccountContact contact)
    {
        // Mark the contact as overridden when manually updated
        contact.IsOverridden = true;

        await Connection.UpdateAsync(contact, typeof(AccountContact)).ConfigureAwait(false);

        return contact;
    }

    public async Task DeleteContactAsync(string address)
    {
        var contact = await GetAddressInformationByAddressAsync(address).ConfigureAwait(false);

        if (contact != null && !contact.IsRootContact)
        {
            await Connection.DeleteAsync<AccountContact>(contact.Address).ConfigureAwait(false);
        }
    }

    public async Task DeleteContactsAsync(IEnumerable<string> addresses)
    {
        var addressList = addresses.Where(a => !string.IsNullOrEmpty(a)).ToList();
        if (addressList.Count == 0) return;

        var placeholders = string.Join(",", addressList.Select((_, i) => "?"));
        await Connection.ExecuteAsync(
            $"DELETE FROM AccountContact WHERE Address IN ({placeholders}) AND IsRootContact = 0",
            addressList.Cast<object>().ToArray()
        ).ConfigureAwait(false);
    }
}
