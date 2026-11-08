#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Services.Voice;

/// <summary>
/// Personal mode names people the server resolved from the brain: the command carries the person's name and
/// <see cref="VoiceCommand.People"/> maps it to the hash of their address. The device finds the address whose hash
/// matches among its contacts and correspondents; it never matches nicknames or relationships itself.
/// </summary>
public sealed partial class VoicePlanExecutor
{
    private const int HistoryScanLimit = 2000;

    /// <summary>The address <paramref name="name"/> stands for when the server resolved it, otherwise null.</summary>
    private async Task<string?> ResolvePersonAsync(string? name, IReadOnlyList<PersonIdentity>? people)
    {
        if (string.IsNullOrWhiteSpace(name) || people is not { Count: > 0 })
            return null;

        var trimmed = name.Trim();
        var identity = people.FirstOrDefault(person => string.Equals(person.Name?.Trim(), trimmed, StringComparison.CurrentCultureIgnoreCase));
        if (identity is null || string.IsNullOrWhiteSpace(identity.AddressHash))
            return null;

        var hash = identity.AddressHash.Trim().ToLowerInvariant();
        try
        {
            var contacts = await contactService.ResolveRecipientCandidatesAsync(null, trimmed, 50).ConfigureAwait(false) ?? [];
            foreach (var contact in contacts)
            {
                var addresses = (contact.EmailAddresses ?? []).Select(static entry => entry.Address).Append(contact.PrimaryEmailAddress);
                var match = addresses.FirstOrDefault(address => !string.IsNullOrWhiteSpace(address) && VoiceBrainHash.Address(address) == hash);
                if (match is not null)
                    return match;
            }

            if (services.GetService<IRecipientHistoryService>() is not { } history || services.GetService<IAccountService>() is not { } accounts)
                return null;

            foreach (var account in await accounts.GetAccountsAsync().ConfigureAwait(false) ?? [])
            {
                var rows = await history.GetMostFrequentAsync(account.Id, HistoryScanLimit).ConfigureAwait(false) ?? [];
                var row = rows.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Address) && VoiceBrainHash.Address(row.Address) == hash);
                if (row is not null)
                    return row.Address;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not find the person a voice command named.");
        }

        return null;
    }
}
