using System;
using System.Collections.Concurrent;
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
    /// <summary>
    /// In-memory contact cache keyed by e-mail address (case-insensitive).
    /// Eliminates per-mail DB round-trips during bulk mail list loads.
    /// Entries are added on fetch and invalidated on update/delete.
    /// </summary>
    private readonly ConcurrentDictionary<string, AccountContact> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public ContactService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<AccountContact> CreateNewContactAsync(string address, string displayName)
    {
        var contact = new AccountContact() { Address = address, Name = displayName };

        await Connection.InsertAsync(contact, typeof(AccountContact)).ConfigureAwait(false);

        _cache[address] = contact;
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

    public async Task<AccountContact> GetAddressInformationByAddressAsync(string address)
    {
        if (string.IsNullOrEmpty(address))
            return null;

        if (_cache.TryGetValue(address, out var cached))
            return cached;

        var contact = await Connection.Table<AccountContact>().FirstOrDefaultAsync(a => a.Address == address).ConfigureAwait(false);

        if (contact != null)
            _cache[contact.Address] = contact;

        return contact;
    }

    public async Task<List<AccountContact>> GetContactsByAddressesAsync(IEnumerable<string> addresses)
    {
        var addressList = addresses?.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addressList == null || addressList.Count == 0)
            return new List<AccountContact>();

        var result = new List<AccountContact>(addressList.Count);
        var missing = new List<string>();

        foreach (var addr in addressList)
        {
            if (_cache.TryGetValue(addr, out var cached))
                result.Add(cached);
            else
                missing.Add(addr);
        }

        if (missing.Count > 0)
        {
            var placeholders = string.Join(",", missing.Select(_ => "?"));
            var fromDb = await Connection.QueryAsync<AccountContact>(
                $"SELECT * FROM AccountContact WHERE Address IN ({placeholders})",
                missing.Cast<object>().ToArray()).ConfigureAwait(false);

            foreach (var contact in fromDb)
            {
                _cache[contact.Address] = contact;
                result.Add(contact);
            }
        }

        return result;
    }

    public async Task SaveAddressInformationAsync(MimeMessage message)
    {
        if (message == null) return;

        // Save all individual contacts (GetRecipients expands GroupAddress members automatically).
        var contacts = message
            .GetRecipients(true)
            .Where(a => !string.IsNullOrWhiteSpace(a.Address))
            .Select(a => new AccountContact
            {
                Name = string.IsNullOrWhiteSpace(a.Name) ? a.Address : a.Name,
                Address = a.Address
            });

        await SaveAddressInformationInternalAsync(contacts).ConfigureAwait(false);

        // Persist named RFC 2822 group structure (e.g. "Team Alpha: alice@x.com, bob@x.com;").
        await SaveGroupsFromInternetAddressesAsync(message.To, message.Cc, message.Bcc).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects <see cref="GroupAddress"/> entries in the supplied address lists and upserts
    /// corresponding <see cref="ContactGroup"/> and <see cref="ContactGroupMember"/> rows.
    /// Individual member contacts are expected to already be saved by the caller.
    /// </summary>
    private async Task SaveGroupsFromInternetAddressesAsync(params InternetAddressList[] addressLists)
    {
        foreach (var list in addressLists)
        {
            if (list == null) continue;

            foreach (var address in list)
            {
                if (address is not GroupAddress group) continue;
                var groupName = group.Name?.Trim();
                if (!ShouldPersistGroupName(groupName)) continue;

                var memberAddresses = group.Members
                    .OfType<MailboxAddress>()
                    .Where(m => !string.IsNullOrWhiteSpace(m.Address))
                    .Select(m => new { Address = m.Address.Trim(), m.Name })
                    .Where(m => ShouldPersistAutoCollectedContact(m.Address, m.Name))
                    .Select(m => m.Address)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (memberAddresses.Count == 0) continue;

                var contactGroup = await GetOrCreateGroupByNameAsync(groupName!).ConfigureAwait(false);

                // Fetch current members once to avoid duplicate inserts.
                var existingMembers = await Connection.QueryAsync<ContactGroupMember>(
                    "SELECT * FROM ContactGroupMember WHERE GroupId = ?", contactGroup.Id
                ).ConfigureAwait(false);

                var existingAddresses = new HashSet<string>(
                    existingMembers.Select(m => m.MemberAddress),
                    StringComparer.OrdinalIgnoreCase
                );

                foreach (var memberAddress in memberAddresses)
                {
                    if (!existingAddresses.Contains(memberAddress))
                        await AddGroupMemberAsync(contactGroup.Id, memberAddress).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Returns the <see cref="ContactGroup"/> with the given name, creating it if it does not exist.
    /// </summary>
    private async Task<ContactGroup> GetOrCreateGroupByNameAsync(string name)
    {
        var existing = await Connection.QueryAsync<ContactGroup>(
            "SELECT * FROM ContactGroup WHERE Name = ? LIMIT 1", name
        ).ConfigureAwait(false);

        return existing.Count > 0
            ? existing[0]
            : await CreateGroupAsync(name).ConfigureAwait(false);
    }

    public async Task SaveAddressInformationAsync(IEnumerable<AccountContact> contacts)
    {
        if (contacts == null) return;

        await SaveAddressInformationInternalAsync(contacts).ConfigureAwait(false);
    }

    private async Task SaveAddressInformationInternalAsync(IEnumerable<AccountContact> contacts)
    {
        var normalizedContacts = contacts
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Address))
            .Select(a => new AccountContact
            {
                Address = a.Address.Trim(),
                Name = string.IsNullOrWhiteSpace(a.Name) ? a.Address.Trim() : a.Name.Trim()
            })
            .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (normalizedContacts.Count == 0) return;

        try
        {
            var noiseAddresses = normalizedContacts
                .Where(a => !ShouldPersistAutoCollectedContact(a.Address, a.Name))
                .Select(a => a.Address)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (noiseAddresses.Count > 0)
                await DeleteAutoCapturedContactsAsync(noiseAddresses).ConfigureAwait(false);

            var addressInformations = normalizedContacts
                .Where(a => ShouldPersistAutoCollectedContact(a.Address, a.Name))
                .ToList();

            if (addressInformations.Count == 0) return;

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

                // Update cache for inserted and updated contacts.
                foreach (var c in toInsert)
                    _cache[c.Address] = c;
                foreach (var c in toUpdate)
                    _cache[c.Address] = c;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to batch save contact information to the database.");
        }
    }

    private async Task DeleteAutoCapturedContactsAsync(IReadOnlyList<string> addresses)
    {
        if (addresses == null || addresses.Count == 0) return;

        var placeholders = string.Join(",", addresses.Select(_ => "?"));
        await Connection.ExecuteAsync(
            $"DELETE FROM AccountContact WHERE Address IN ({placeholders}) AND IsRootContact = 0 AND IsOverridden = 0",
            addresses.Cast<object>().ToArray()
        ).ConfigureAwait(false);

        foreach (var address in addresses)
            _cache.TryRemove(address, out _);
    }

    private static bool ShouldPersistAutoCollectedContact(string address, string displayName)
    {
        if (!TryGetLocalPart(address, out var localPart))
            return false;

        var localPartLower = localPart.ToLowerInvariant();

        // High confidence machine-generated senders/recipients that should not pollute the contact list.
        if (localPartLower.StartsWith("reply+", StringComparison.Ordinal) ||
            localPartLower.Contains("noreply", StringComparison.Ordinal) ||
            localPartLower.Contains("no-reply", StringComparison.Ordinal) ||
            localPartLower.Contains("donotreply", StringComparison.Ordinal) ||
            localPartLower.Contains("do-not-reply", StringComparison.Ordinal) ||
            localPartLower == "mailer-daemon" ||
            localPartLower == "postmaster")
        {
            return false;
        }

        // Generic notification mailboxes are only persisted when they look human-assigned.
        if (localPartLower is "notification" or "notifications" or "updates" or "digest")
            return !IsLikelyMachineGeneratedDisplayName(displayName);

        return true;
    }

    private static bool ShouldPersistGroupName(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return false;

        var trimmed = groupName.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower.Contains("issue #", StringComparison.Ordinal) ||
            lower.Contains("pull request #", StringComparison.Ordinal) ||
            lower.Contains("discussion #", StringComparison.Ordinal) ||
            lower.Contains("notification", StringComparison.Ordinal))
        {
            return false;
        }

        // GitHub-like dynamic repository labels: [owner/repository]
        if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
            trimmed.Contains('/') &&
            trimmed.Contains("]"))
        {
            return false;
        }

        return true;
    }

    private static bool IsLikelyMachineGeneratedDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;

        var trimmed = displayName.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower.Contains("notification", StringComparison.Ordinal) ||
            lower.Contains("issue #", StringComparison.Ordinal) ||
            lower.Contains("pull request #", StringComparison.Ordinal) ||
            lower.Contains("discussion #", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.StartsWith("[", StringComparison.Ordinal) &&
               trimmed.Contains('/') &&
               trimmed.Contains("]");
    }

    private static bool TryGetLocalPart(string address, out string localPart)
    {
        localPart = string.Empty;

        if (string.IsNullOrWhiteSpace(address))
            return false;

        var trimmed = address.Trim();
        var atIndex = trimmed.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == trimmed.Length - 1)
            return false;

        localPart = trimmed[..atIndex];
        return !string.IsNullOrWhiteSpace(localPart);
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

        _cache[contact.Address] = contact;
        return contact;
    }

    public async Task DeleteContactAsync(string address)
    {
        var contact = await GetAddressInformationByAddressAsync(address).ConfigureAwait(false);

        if (contact != null && !contact.IsRootContact)
        {
            await Connection.DeleteAsync<AccountContact>(contact.Address).ConfigureAwait(false);
            _cache.TryRemove(address, out _);
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

        foreach (var addr in addressList)
            _cache.TryRemove(addr, out _);
    }

    #region Group / Distribution List

    public Task<List<ContactGroup>> GetGroupsAsync()
        => Connection.Table<ContactGroup>().OrderBy(g => g.Name).ToListAsync();

    public async Task<ContactGroup> CreateGroupAsync(string name, string description = null)
    {
        var group = new ContactGroup { Id = Guid.NewGuid(), Name = name, Description = description };
        await Connection.InsertAsync(group, typeof(ContactGroup)).ConfigureAwait(false);
        return group;
    }

    public async Task DeleteGroupAsync(Guid groupId)
    {
        // Remove members first to avoid orphaned rows.
        await Connection.ExecuteAsync(
            "DELETE FROM ContactGroupMember WHERE GroupId = ?", groupId).ConfigureAwait(false);
        await Connection.DeleteAsync<ContactGroup>(groupId).ConfigureAwait(false);
    }

    public async Task<List<AccountContact>> GetGroupMembersAsync(Guid groupId)
    {
        var members = await Connection.QueryAsync<ContactGroupMember>(
            "SELECT * FROM ContactGroupMember WHERE GroupId = ?", groupId).ConfigureAwait(false);

        var addresses = members.Select(m => m.MemberAddress).ToList();
        return await GetContactsByAddressesAsync(addresses).ConfigureAwait(false);
    }

    public async Task AddGroupMemberAsync(Guid groupId, string memberAddress)
    {
        var member = new ContactGroupMember { GroupId = groupId, MemberAddress = memberAddress };
        await Connection.InsertAsync(member, typeof(ContactGroupMember)).ConfigureAwait(false);
    }

    public async Task RemoveGroupMemberAsync(Guid groupId, string memberAddress)
    {
        await Connection.ExecuteAsync(
            "DELETE FROM ContactGroupMember WHERE GroupId = ? AND MemberAddress = ?",
            groupId, memberAddress).ConfigureAwait(false);
    }

    public Task<List<AccountContact>> ExpandGroupAsync(Guid groupId)
        => GetGroupMembersAsync(groupId);

    #endregion
}

