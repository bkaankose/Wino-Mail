using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Mapi.Rops;

namespace Wino.Mapi.Autocomplete;

/// <summary>
/// One name in the autocomplete list. A row of MAPI properties, of which a handful mean something
/// to us and the rest belong to Outlook and are carried through untouched.
/// </summary>
public sealed class AutocompleteEntry
{
    /// <summary>The key of a row, which the format says must be its first property.</summary>
    public const uint NickName = 0x6001001F;

    // The four below are the library's own tags, named once in PropertyTags; only the two 0x6xxx
    // nickname-cache tags are particular to this format.
    public const uint DisplayNameTag = PropertyTags.DisplayName;
    public const uint EmailAddressTag = PropertyTags.EmailAddress;
    public const uint AddressTypeTag = PropertyTags.AddressType;
    public const uint SmtpAddressTag = PropertyTags.SmtpAddress;
    public const uint DropdownDisplayNameTag = 0x6003001F;

    /// <summary>
    /// What orders the list. Outlook raises it by 0x2000 whenever a message goes to the address and
    /// lets it fall away over time, which is why the names you actually write to rise to the top.
    /// </summary>
    public const uint WeightTag = 0x60040003;

    /// <summary>How much a single send is worth, matching Outlook so the two agree about an order.</summary>
    public const int WeightPerSend = 0x2000;

    /// <summary>The format's own bounds: below one is invalid, and it is a signed value.</summary>
    public const int MinimumWeight = 1;

    public AutocompleteEntry(List<AutocompleteProperty> properties)
    {
        Properties = properties;
    }

    public List<AutocompleteProperty> Properties { get; }

    public string? NickNameText => Find(NickName)?.AsString();

    public string? DisplayName => Find(DisplayNameTag)?.AsString();

    public string? DropdownDisplayName => Find(DropdownDisplayNameTag)?.AsString();

    /// <summary>
    /// The address to write to. Outlook stores the SMTP address separately from the address a row
    /// was created with, and for an Exchange recipient the latter is an X.500 path rather than
    /// anything a person would recognise - so prefer the SMTP one, and only fall back when the row
    /// says it is SMTP anyway.
    /// </summary>
    public string? SmtpAddress
    {
        get
        {
            var smtp = Find(SmtpAddressTag)?.AsString();

            if (!string.IsNullOrWhiteSpace(smtp))
                return smtp;

            var type = Find(AddressTypeTag)?.AsString();

            return string.Equals(type, "SMTP", StringComparison.OrdinalIgnoreCase)
                ? Find(EmailAddressTag)?.AsString()
                : null;
        }
    }

    public int Weight
    {
        get => Find(WeightTag)?.AsInt32() ?? MinimumWeight;
        set
        {
            var existing = Find(WeightTag);

            if (existing is not null)
                existing.SetInt32(value);
            else
                Properties.Add(AutocompleteProperty.Int32(WeightTag, value));
        }
    }

    /// <summary>One more send's worth, stopping short of overflowing the signed maximum.</summary>
    public static int Heavier(int weight)
        => weight > int.MaxValue - WeightPerSend ? int.MaxValue : Math.Max(weight, MinimumWeight) + WeightPerSend;

    public AutocompleteProperty? Find(uint tag) => Properties.FirstOrDefault(p => p.Tag == tag);

    /// <summary>
    /// A new row for an address we have just written to. The nick name comes first because the
    /// format requires it, and the display name doubles as what the dropdown shows when Outlook has
    /// not supplied something different.
    /// </summary>
    public static AutocompleteEntry Create(string smtpAddress, string? displayName = null)
    {
        var shown = string.IsNullOrWhiteSpace(displayName) ? smtpAddress : displayName;

        return new AutocompleteEntry(
        [
            AutocompleteProperty.Unicode(NickName, shown),
            AutocompleteProperty.Unicode(DisplayNameTag, shown),
            AutocompleteProperty.Unicode(DropdownDisplayNameTag, shown),
            AutocompleteProperty.Unicode(EmailAddressTag, smtpAddress),
            AutocompleteProperty.Unicode(AddressTypeTag, "SMTP"),
            AutocompleteProperty.Unicode(SmtpAddressTag, smtpAddress),
            AutocompleteProperty.Int32(WeightTag, MinimumWeight + WeightPerSend),
        ]);
    }
}
