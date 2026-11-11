using System;

namespace Wino.Core.Domain.Models.CardDav;

/// <summary>
/// Compares a contact's UID with a group's member reference. RFC 6350 recommends urn:uuid UIDs
/// (section 6.7.6) and MEMBER refers to the member by that URI (section 6.6.5); Apple's vCard 3
/// groups write urn:uuid: before a bare UID. Both spellings name the same contact.
/// </summary>
public static class VCardUid
{
    private const string UuidPrefix = "urn:uuid:";

    public static string Normalize(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.StartsWith(UuidPrefix, StringComparison.OrdinalIgnoreCase) ? trimmed[UuidPrefix.Length..] : trimmed;
    }

    /// <summary>The member reference for a UID: the UID itself when it is already a URI, otherwise its urn:uuid form.</summary>
    public static string ToMemberReference(string uid)
    {
        var trimmed = uid?.Trim() ?? string.Empty;
        return trimmed.Contains(':') ? trimmed : UuidPrefix + trimmed;
    }
}
