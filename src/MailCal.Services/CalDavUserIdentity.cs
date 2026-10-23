#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MailCal.Services;

/// <summary>
/// The user as a CalDAV scheduling participant (RFC 6638). The server's
/// <c>calendar-user-address-set</c> lists every address that means "this user": mail addresses,
/// principal paths and <c>urn:uuid:</c> values. ORGANIZER needs a mail address, while finding the
/// user's own ATTENDEE line may match any of them.
/// </summary>
public sealed class CalDavUserIdentity
{
    private const string MailtoPrefix = "mailto:";

    private readonly HashSet<string> _emails;
    private readonly HashSet<string> _rawAddresses;

    private CalDavUserIdentity(string organizerAddress, HashSet<string> emails, HashSet<string> rawAddresses)
    {
        OrganizerAddress = organizerAddress;
        _emails = emails;
        _rawAddresses = rawAddresses;
    }

    /// <summary>The bare mail address written as ORGANIZER on events the user organizes; empty when none is known.</summary>
    public string OrganizerAddress { get; }

    /// <summary>Every mail address of the user, without the <c>mailto:</c> prefix.</summary>
    public IReadOnlyCollection<string> EmailAddresses => _emails;

    /// <summary>
    /// Picks the ORGANIZER address: the <c>mailto:</c> entry equal to the account address, else the
    /// first <c>mailto:</c> entry, else the account address. <c>mailto:null</c> (Yahoo), principal
    /// paths and <c>urn:uuid:</c> values are never used as ORGANIZER.
    /// </summary>
    public static CalDavUserIdentity Resolve(IEnumerable<string>? calendarUserAddresses, string? accountAddress)
    {
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mailtoEmails = new List<string>();

        foreach (var address in calendarUserAddresses ?? Array.Empty<string>())
        {
            var trimmed = address?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var email = ExtractEmail(trimmed);

            if (trimmed.StartsWith(MailtoPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // mailto:null and other values without a mailbox are not addresses of anyone.
                if (email.Length == 0)
                    continue;

                mailtoEmails.Add(email);
            }

            rawAddresses.Add(trimmed);

            if (email.Length > 0)
                emails.Add(email);
        }

        var account = ExtractEmail(accountAddress);
        if (account.Length > 0)
            emails.Add(account);

        var organizer = mailtoEmails.FirstOrDefault(email => string.Equals(email, account, StringComparison.OrdinalIgnoreCase))
                        ?? mailtoEmails.FirstOrDefault()
                        ?? account;

        return new CalDavUserIdentity(organizer, emails, rawAddresses);
    }

    /// <summary>
    /// True when a calendar user address (an ATTENDEE or ORGANIZER value, with or without
    /// <c>mailto:</c>) is one of the user's addresses. Comparison ignores case.
    /// </summary>
    public bool IsUser(string? calendarUserAddress)
    {
        var trimmed = calendarUserAddress?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        if (_rawAddresses.Contains(trimmed))
            return true;

        var email = ExtractEmail(trimmed);
        return email.Length > 0 && _emails.Contains(email);
    }

    public bool IsUser(Uri? calendarUserAddress)
        => calendarUserAddress != null && IsUser(calendarUserAddress.OriginalString);

    /// <summary>
    /// The ORGANIZER address for an event Mail-Cal writes: the resolved identity when the event has no
    /// organizer yet or the organizer is the user, otherwise the existing organizer unchanged.
    /// </summary>
    public string ResolveOrganizerAddress(string? currentOrganizer)
    {
        var current = ExtractEmail(currentOrganizer);
        if (current.Length == 0 || IsUser(current))
            return OrganizerAddress.Length > 0 ? OrganizerAddress : current;

        return current;
    }

    /// <summary>The mailbox of a calendar user address, or empty for <c>mailto:null</c> and non-mail values.</summary>
    public static string ExtractEmail(string? calendarUserAddress)
    {
        var value = calendarUserAddress?.Trim() ?? string.Empty;
        if (value.StartsWith(MailtoPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[MailtoPrefix.Length..].Trim();

        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && !value.Contains(' ') && !value.Contains('/')
            ? value
            : string.Empty;
    }
}
