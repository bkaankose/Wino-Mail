using System;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>
/// The local contact's fields in the slots an Exchange contact has for them, shared by the EWS and MAPI writes so both
/// transports store a contact the same way: three e-mail addresses, two work and two home numbers and one mobile
/// number (as ActiveSync writes them), and the category names.
/// </summary>
public static class ExchangeContactFields
{
    /// <summary>The three e-mail slots: the primary address first, then by order, as the reads fill them.</summary>
    public static string?[] Emails(AccountContact item)
    {
        var addresses = (item.EmailAddresses ?? [])
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.Order)
            .Select(a => Clean(a.Address))
            .OfType<string>()
            .Take(3)
            .ToList();

        return [.. Enumerable.Range(0, 3).Select(i => i < addresses.Count ? addresses[i] : null)];
    }

    /// <summary>The first <paramref name="count"/> numbers of the kind, in order, padded with nulls.</summary>
    public static string?[] Phones(AccountContact item, ContactPhoneKind kind, int count)
    {
        var numbers = (item.PhoneNumbers ?? [])
            .Where(p => p.Kind == kind)
            .OrderBy(p => p.Order)
            .Select(p => Clean(p.Number))
            .OfType<string>()
            .Take(count)
            .ToList();

        return [.. Enumerable.Range(0, count).Select(i => i < numbers.Count ? numbers[i] : null)];
    }

    /// <summary>The contact's category names as a write carries them: the reported names, else the loaded categories.</summary>
    public static string[] CategoryNames(AccountContact item)
        => (item.CategoryNames ?? item.Categories?.Select(category => category.Name).ToList() ?? [])
            .Select(Clean)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
