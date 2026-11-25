using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// What Settings UserInformation Get says about the mailbox. 12.x and 14.0 answer with a flat EmailAddresses list;
/// 14.1 and later group addresses per account (the mailbox and any delegated ones) with the display name.
/// </summary>
internal sealed record EasUserInformation(string? DisplayName, string? PrimarySmtpAddress, IReadOnlyList<string> Addresses)
{
    private static readonly XNamespace Settings = "Settings:";
    private static readonly XNamespace Resolve = "ResolveRecipients:";

    internal static EasUserInformation Parse(XElement userInformation, string accountAddress)
    {
        var get = userInformation.Element(Settings + "Get");
        if (get == null)
            return new EasUserInformation(null, null, []);

        var accounts = get.Element(Settings + "Accounts")?.Elements(Settings + "Account").ToList() ?? [];
        XElement? source;
        if (accounts.Count > 0)
        {
            // The mailbox's own account carries no AccountId; a delegated mailbox has one. Failing both, the one that
            // holds the account address.
            source = accounts.FirstOrDefault(account => string.IsNullOrEmpty(account.Element(Settings + "AccountId")?.Value))
                     ?? accounts.FirstOrDefault(account => AddressesOf(account.Element(Settings + "EmailAddresses"))
                         .Contains(accountAddress, StringComparer.OrdinalIgnoreCase))
                     ?? accounts[0];
        }
        else
        {
            source = get;
        }

        var emailAddresses = source.Element(Settings + "EmailAddresses");
        var primary = Clean(emailAddresses?.Element(Settings + "PrimarySmtpAddress")?.Value);
        var addresses = AddressesOf(emailAddresses);
        if (primary != null && !addresses.Contains(primary, StringComparer.OrdinalIgnoreCase))
            addresses.Insert(0, primary);

        var displayName = source.Element(Settings + "UserDisplayName")?.Value?.Trim();
        return new EasUserInformation(string.IsNullOrEmpty(displayName) ? null : displayName, primary, addresses);
    }

    /// <summary>
    /// The photo in a ResolveRecipients recipient: downloaded, confirmed absent (status 173, the directory has none),
    /// or null when the answer says nothing usable, so the stored picture stays.
    /// </summary>
    internal static ProfilePictureFetchResult? ReadPicture(XElement? recipient)
    {
        var picture = recipient?.Element(Resolve + "Picture");
        switch (picture?.Element(Resolve + "Status")?.Value)
        {
            case "1":
                var data = picture.Element(Resolve + "Data")?.Value;
                if (string.IsNullOrEmpty(data))
                    return null;
                try
                {
                    return ProfilePictureFetchResult.Downloaded(Convert.FromBase64String(data));
                }
                catch (FormatException)
                {
                    return null;
                }
            case "173":
                return ProfilePictureFetchResult.ConfirmedAbsent;
            default:
                return null;
        }
    }

    private static List<string> AddressesOf(XElement? emailAddresses)
        => emailAddresses?.Elements(Settings + "SMTPAddress")
               .Select(element => Clean(element.Value))
               .OfType<string>()
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList()
           ?? [];

    private static string? Clean(string? address)
    {
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (trimmed.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[5..].Trim();

        return trimmed.Contains('@') ? trimmed : null;
    }
}
