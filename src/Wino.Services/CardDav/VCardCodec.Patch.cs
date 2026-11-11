using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.CardDav;

/// <summary>
/// Writes a contact edit into a card. Only what differs between the contact Wino started from and
/// the edited one is written; every other property, parameter and value stays as the server had it.
/// </summary>
public sealed partial class VCardCodec
{
    private static readonly string[] PhoneKindTypes = ["home", "work", "cell", "mobile"];
    private static readonly string[] AddressKindTypes = ["home", "work"];

    public void Patch(VCardDocument document, AccountContact contact) => Patch(document, contact, null);

    public void Patch(VCardDocument document, AccountContact contact, AccountContact original)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(contact);

        // Without the contact the edit started from, the card itself is the starting point.
        var current = Project(document);
        var origin = original ?? current;
        var version = document.Version;

        PatchText(document, "FN", origin.DisplayValue, contact.DisplayValue);
        PatchComponents(document, "N", 5, keepWhenEmpty: true,
            (0, origin.Surname, contact.Surname),
            (1, origin.GivenName, contact.GivenName),
            (2, origin.MiddleName, contact.MiddleName),
            (3, origin.HonorificPrefix, contact.HonorificPrefix),
            (4, origin.HonorificSuffix, contact.HonorificSuffix));
        PatchText(document, "NICKNAME", origin.Nickname, contact.Nickname);
        PatchText(document, "SORT-STRING", origin.FileAs, contact.FileAs);
        PatchComponents(document, "ORG", 1, keepWhenEmpty: false,
            (0, origin.CompanyName, contact.CompanyName),
            (1, origin.Department, contact.Department));
        PatchText(document, "TITLE", origin.JobTitle, contact.JobTitle);
        PatchText(document, "ROLE", origin.Profession, contact.Profession);
        PatchText(document, "NOTE", origin.Notes, contact.Notes);
        PatchText(document, "URL", origin.Website, contact.Website, isUri: true);
        PatchBirthday(document, origin, contact);

        PatchList(document, "EMAIL", _ => true, current.EmailAddresses,
            origin.EmailAddresses, contact.EmailAddresses?.OrderBy(item => item.Order).ToList(),
            item => item.Id,
            (left, right) => Same(left.Address, right.Address) && SameToken(left.Label, right.Label) && left.IsPrimary == right.IsPrimary,
            (property, before, after) =>
            {
                if (!Same(before.Address, after.Address)) SetValue(property, EscapeText(after.Address?.Trim()));
                if (!SameToken(before.Label, after.Label)) ReplaceTypes(property, [before.Label], after.Label);
                if (before.IsPrimary != after.IsPrimary) SetPreference(property, after.IsPrimary, version);
            },
            item =>
            {
                var property = Property("EMAIL", EscapeText(item.Address?.Trim()));
                AddType(property, item.Label);
                if (item.IsPrimary) SetPreference(property, true, version);
                return property;
            });

        PatchList(document, "TEL", _ => true, current.PhoneNumbers,
            origin.PhoneNumbers, contact.PhoneNumbers?.OrderBy(item => item.Order).ToList(),
            item => item.Id,
            (left, right) => Same(left.Number, right.Number) && left.Kind == right.Kind && left.IsPrimary == right.IsPrimary,
            (property, before, after) =>
            {
                if (!Same(before.Number, after.Number)) SetValue(property, PhoneValue(property, after.Number));
                if (before.Kind != after.Kind) ReplaceTypes(property, PhoneKindTypes, PhoneType(after.Kind));
                if (before.IsPrimary != after.IsPrimary) SetPreference(property, after.IsPrimary, version);
            },
            item =>
            {
                var property = Property("TEL", EscapeText(item.Number?.Trim()));
                AddType(property, PhoneType(item.Kind));
                if (item.IsPrimary) SetPreference(property, true, version);
                return property;
            });

        PatchList(document, "ADR", _ => true, current.PostalAddresses,
            origin.PostalAddresses, contact.PostalAddresses,
            item => item.Id,
            (left, right) => left.Kind == right.Kind && Same(left.PostOfficeBox, right.PostOfficeBox) && Same(left.Street, right.Street) &&
                             Same(left.City, right.City) && Same(left.Region, right.Region) && Same(left.PostalCode, right.PostalCode) &&
                             Same(left.Country, right.Country),
            (property, before, after) =>
            {
                ReplaceComponents(property, 7,
                    (0, before.PostOfficeBox, after.PostOfficeBox),
                    (2, before.Street, after.Street),
                    (3, before.City, after.City),
                    (4, before.Region, after.Region),
                    (5, before.PostalCode, after.PostalCode),
                    (6, before.Country, after.Country));
                if (before.Kind != after.Kind) ReplaceTypes(property, AddressKindTypes, AddressType(after.Kind));
            },
            item =>
            {
                var property = Property("ADR", JoinStructured(item.PostOfficeBox, null, item.Street, item.City, item.Region, item.PostalCode, item.Country));
                AddType(property, AddressType(item.Kind));
                return property;
            });

        PatchList(document, "IMPP", _ => true, current.ImAddresses,
            origin.ImAddresses, contact.ImAddresses?.OrderBy(item => item.Order).ToList(),
            item => item.Id,
            (left, right) => Same(left.Address, right.Address),
            (property, _, after) => SetValue(property, UriValue(after.Address)),
            item => Property("IMPP", UriValue(item.Address)));

        // Only relations of a kind Wino knows are part of the contact; any other RELATED line is left alone.
        PatchList(document, "RELATED", property => RelationKind(property) is not null, current.Relations,
            origin.Relations, contact.Relations?.OrderBy(item => item.Order).ToList(),
            item => item.Id,
            (left, right) => left.Kind == right.Kind && Same(left.Name, right.Name),
            (property, before, after) =>
            {
                if (!Same(before.Name, after.Name)) SetRelationValue(property, after.Name, version);
                if (before.Kind != after.Kind) ReplaceTypes(property, [before.Kind.ToString()], after.Kind.ToString().ToLowerInvariant());
            },
            item =>
            {
                var property = Property("RELATED", string.Empty);
                AddType(property, item.Kind.ToString());
                SetRelationValue(property, item.Name, version);
                return property;
            });
    }

    private static bool Same(string left, string right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.Ordinal);

    private static bool SameToken(string left, string right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A single-valued property: the first one is edited, any further ones are left alone.</summary>
    private static void PatchText(VCardDocument document, string name, string before, string after, bool isUri = false)
    {
        if (Same(before, after))
            return;

        var property = First(document, name);
        if (string.IsNullOrWhiteSpace(after))
        {
            if (property is not null)
                document.Properties.Remove(property);
            return;
        }

        var value = isUri ? UriValue(after) : EscapeText(after.Trim());
        if (property is null)
            Insert(document, Property(name, value));
        else
            SetValue(property, value);
    }

    /// <summary>
    /// A structured property (RFC 6350 section 3.3): only the changed components of the first
    /// property are replaced; components Wino does not model keep their escaped text.
    /// </summary>
    private static void PatchComponents(VCardDocument document, string name, int minimumComponents, bool keepWhenEmpty,
        params (int Index, string Before, string After)[] components)
    {
        if (components.All(component => Same(component.Before, component.After)))
            return;

        var property = First(document, name);
        if (property is null)
        {
            if (components.All(component => string.IsNullOrWhiteSpace(component.After)))
                return;

            property = Property(name, string.Empty);
            Insert(document, property);
        }

        ReplaceComponents(property, minimumComponents, components);
        if (!keepWhenEmpty && SplitEscaped(property.Value, ';').All(string.IsNullOrWhiteSpace))
            document.Properties.Remove(property);
    }

    private static void ReplaceComponents(VCardProperty property, int minimumComponents, params (int Index, string Before, string After)[] components)
    {
        var parts = string.IsNullOrEmpty(property.Value) ? [] : SplitRaw(property.Value, ';');
        var required = Math.Max(minimumComponents, components.Max(component => component.Index) + 1);
        while (parts.Count < required)
            parts.Add(string.Empty);

        foreach (var (index, before, after) in components)
        {
            if (!Same(before, after))
                parts[index] = EscapeText(after?.Trim()) ?? string.Empty;
        }

        SetValue(property, string.Join(';', parts));
    }

    private static void PatchBirthday(VCardDocument document, AccountContact before, AccountContact after)
    {
        if (before.BirthdayYear == after.BirthdayYear && before.BirthdayMonth == after.BirthdayMonth && before.BirthdayDay == after.BirthdayDay)
            return;

        var property = First(document, "BDAY");
        var value = FormatBirthday(after, document.Version);
        if (value is null)
        {
            if (property is not null)
                document.Properties.Remove(property);
            return;
        }

        if (property is null)
        {
            Insert(document, Property("BDAY", value));
            return;
        }

        // The written value is a date; a text value type or Apple's year placeholder no longer applies.
        property.Parameters.RemoveAll(parameter => parameter.Name is "VALUE" or "X-APPLE-OMIT-YEAR");
        SetValue(property, value);
    }

    /// <summary>
    /// A repeated property. Items of the starting contact are matched to the card's properties by
    /// their values; an item the edit removed loses its property, a changed one has only the changed
    /// parts rewritten, and a new one is added after the last property of the same name.
    /// </summary>
    private static void PatchList<T>(
        VCardDocument document,
        string name,
        Func<VCardProperty, bool> isModeled,
        IReadOnlyList<T> projected,
        IReadOnlyList<T> before,
        IReadOnlyList<T> after,
        Func<T, Guid> id,
        Func<T, T, bool> same,
        Action<VCardProperty, T, T> update,
        Func<T, VCardProperty> create) where T : class
    {
        before ??= [];
        after ??= [];
        if (before.Count == after.Count && before.Zip(after).All(pair => same(pair.First, pair.Second)))
            return;

        // Project lists the modeled properties in card order, so the two lists line up.
        var properties = All(document, name).Where(isModeled).ToList();
        var free = properties.Zip(projected, (property, item) => (Property: property, Item: item)).ToList();

        var propertyOf = new Dictionary<T, VCardProperty>(ReferenceEqualityComparer.Instance);
        foreach (var item in before)
        {
            var match = free.FindIndex(candidate => same(candidate.Item, item));
            if (match < 0) continue;
            propertyOf[item] = free[match].Property;
            free.RemoveAt(match);
        }

        var pairs = Align(before, after, id, same);
        foreach (var (previous, next) in pairs)
        {
            if (previous is not null && next is null)
            {
                if (propertyOf.TryGetValue(previous, out var removed))
                    document.Properties.Remove(removed);
            }
            else if (previous is not null && next is not null)
            {
                if (same(previous, next)) continue;
                if (propertyOf.TryGetValue(previous, out var existing))
                    update(existing, previous, next);
                else
                    Insert(document, create(next));
            }
            else if (next is not null && !IsEmpty(create(next)))
            {
                Insert(document, create(next));
            }
        }
    }

    private static bool IsEmpty(VCardProperty property) => string.IsNullOrWhiteSpace(property.Value?.Replace(";", string.Empty));

    /// <summary>
    /// Pairs the items before and after an edit: by id first, then equal values, then position.
    /// An unpaired item on the left was removed, one on the right was added.
    /// </summary>
    private static List<(T Before, T After)> Align<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, Guid> id, Func<T, T, bool> same) where T : class
    {
        var pairs = new List<(T, T)>();
        var left = before.ToList();
        var right = after.ToList();

        foreach (var item in after)
        {
            var match = left.FindIndex(candidate => id(candidate) != Guid.Empty && id(candidate) == id(item));
            if (match < 0) continue;
            pairs.Add((left[match], item));
            left.RemoveAt(match);
            right.Remove(item);
        }

        foreach (var item in right.ToList())
        {
            var match = left.FindIndex(candidate => same(candidate, item));
            if (match < 0) continue;
            pairs.Add((left[match], item));
            left.RemoveAt(match);
            right.Remove(item);
        }

        var common = Math.Min(left.Count, right.Count);
        for (var index = 0; index < common; index++)
            pairs.Add((left[index], right[index]));

        pairs.AddRange(left.Skip(common).Select(item => (item, (T)null)));
        pairs.AddRange(right.Skip(common).Select(item => ((T)null, item)));
        return pairs;
    }

    /// <summary>Adds a property after the last one with the same name, or at the end of the card.</summary>
    private static void Insert(VCardDocument document, VCardProperty property)
    {
        var last = document.Properties.FindLastIndex(candidate => candidate.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
        if (last < 0)
            document.Properties.Add(property);
        else
            document.Properties.Insert(last + 1, property);
    }

    /// <summary>A new value is plain text; a quoted-printable encoding of the old value no longer applies.</summary>
    private static void SetValue(VCardProperty property, string value)
    {
        property.Value = value ?? string.Empty;
        property.Parameters.RemoveAll(parameter => parameter.Name == "ENCODING" &&
            parameter.Values.Any(item => item.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Replaces the given TYPE tokens (case-insensitive) with one new token, keeping every other TYPE value.</summary>
    private static void ReplaceTypes(VCardProperty property, IEnumerable<string> replaced, string replacement)
    {
        var tokens = replaced.Where(token => !string.IsNullOrWhiteSpace(token)).ToList();
        foreach (var parameter in property.Parameters.Where(parameter => parameter.Name == "TYPE"))
            parameter.Values.RemoveAll(value => tokens.Contains(value, StringComparer.OrdinalIgnoreCase));
        property.Parameters.RemoveAll(parameter => parameter.Name == "TYPE" && parameter.Values.Count == 0);

        if (string.IsNullOrWhiteSpace(replacement)) return;
        var type = property.Parameters.FirstOrDefault(parameter => parameter.Name == "TYPE");
        if (type is null)
            AddType(property, replacement);
        else if (!type.Values.Contains(replacement, StringComparer.OrdinalIgnoreCase))
            type.Values.Add(replacement.ToLowerInvariant());
    }

    /// <summary>Preference is PREF=1 in vCard 4 (RFC 6350 section 5.3) and TYPE=pref in vCard 3 (RFC 2426 section 3.3.1).</summary>
    private static void SetPreference(VCardProperty property, bool preferred, string version)
    {
        property.Parameters.RemoveAll(parameter => parameter.Name == "PREF");
        foreach (var parameter in property.Parameters.Where(parameter => parameter.Name == "TYPE"))
            parameter.Values.RemoveAll(value => value.Equals("pref", StringComparison.OrdinalIgnoreCase));
        property.Parameters.RemoveAll(parameter => parameter.Name == "TYPE" && parameter.Values.Count == 0);
        if (preferred)
            AddPreference(property, version);
    }

    private static string PhoneType(ContactPhoneKind kind) => kind switch
    {
        ContactPhoneKind.Mobile => "cell",
        ContactPhoneKind.Work => "work",
        _ => "home"
    };

    private static string AddressType(ContactPostalAddressKind kind) => kind switch
    {
        ContactPostalAddressKind.Home => "home",
        ContactPostalAddressKind.Business => "work",
        _ => null
    };

    /// <summary>A number in a VALUE=uri TEL stays a tel: URI (RFC 6350 section 6.4.1, RFC 3966).</summary>
    private static string PhoneValue(VCardProperty property, string number)
    {
        var trimmed = number?.Trim() ?? string.Empty;
        var isUri = property.Parameters.Any(parameter => parameter.Name == "VALUE" &&
                                                          parameter.Values.Any(value => value.Equals("uri", StringComparison.OrdinalIgnoreCase)));
        if (!isUri)
            return EscapeText(trimmed);

        return trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? UriValue(trimmed) : "tel:" + UriValue(trimmed.Replace(' ', '-'));
    }

    /// <summary>
    /// A URI value is written as it is: the text escaping of RFC 6350 section 3.4 does not apply to
    /// URIs (section 4.2), whose ',' and ';' are part of the URI.
    /// </summary>
    private static string UriValue(string value) => value?.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);

    /// <summary>RELATED is a URI by default in vCard 4 (RFC 6350 section 6.6.6); a plain name needs VALUE=text.</summary>
    private static void SetRelationValue(VCardProperty property, string name, string version)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        var isUri = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme) && trimmed.Contains(':');
        property.Parameters.RemoveAll(parameter => parameter.Name == "VALUE");
        if (isUri)
        {
            property.Value = UriValue(trimmed);
            return;
        }

        if (version == "4.0")
        {
            var valueType = new VCardParameter { Name = "VALUE" };
            valueType.Values.Add("text");
            property.Parameters.Add(valueType);
        }

        SetValue(property, EscapeText(trimmed));
    }

    /// <summary>The same rule Project applies: the first TYPE value names the relation.</summary>
    private static ContactRelationKind? RelationKind(VCardProperty property)
        => Enum.TryParse<ContactRelationKind>(ParameterValues(property, "TYPE").FirstOrDefault(), true, out var kind) ? kind : null;

    /// <summary>Components of a structured value with their escaping kept (RFC 6350 section 3.3).</summary>
    private static List<string> SplitRaw(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\') { index++; continue; }
            if (value[index] != separator) continue;
            result.Add(value[start..index]);
            start = index + 1;
        }

        result.Add(value[start..]);
        return result;
    }
}
