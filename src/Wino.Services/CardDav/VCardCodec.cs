using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.CardDav;

public sealed class VCardCodec : IVCardCodec
{
    public VCardDocument Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("The vCard payload is empty.");

        var document = new VCardDocument();
        foreach (var line in Unfold(content))
        {
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = FindDelimiter(line, ':');
            if (separator <= 0)
                continue;

            var header = line[..separator];
            var value = line[(separator + 1)..];
            var headerParts = SplitRespectingQuotes(header, ';');
            if (headerParts.Count == 0)
                continue;

            var property = new VCardProperty { Value = value };
            var name = headerParts[0];
            var groupSeparator = name.IndexOf('.');
            if (groupSeparator > 0)
            {
                property.Group = name[..groupSeparator];
                name = name[(groupSeparator + 1)..];
            }

            property.Name = name.ToUpperInvariant();
            property.OriginalName = name;
            for (var index = 1; index < headerParts.Count; index++)
            {
                var parameterPart = headerParts[index];
                var equals = FindDelimiter(parameterPart, '=');
                var parsedParameterName = equals > 0 ? parameterPart[..equals] : "TYPE";
                var parameter = new VCardParameter
                {
                    Name = parsedParameterName.ToUpperInvariant(),
                    OriginalName = parsedParameterName
                };
                var parameterValue = equals > 0 ? parameterPart[(equals + 1)..] : parameterPart;
                foreach (var item in SplitRespectingQuotes(parameterValue, ','))
                    parameter.Values.Add(DecodeParameter(TrimQuotes(item)));
                property.Parameters.Add(parameter);
            }

            if (property.Name == "VERSION")
                document.Version = value.Trim();

            document.Properties.Add(property);
        }

        if (!document.Properties.Any(property => property.Name == "VERSION"))
            document.Version = "3.0";

        return document;
    }

    public AccountContact Project(VCardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var contact = new AccountContact();
        contact.DisplayName = TextValue(First(document, "FN"));

        var structuredName = Components(First(document, "N")?.Value, ';');
        contact.Surname = structuredName.ElementAtOrDefault(0);
        contact.GivenName = structuredName.ElementAtOrDefault(1);
        contact.MiddleName = structuredName.ElementAtOrDefault(2);
        contact.HonorificPrefix = structuredName.ElementAtOrDefault(3);
        contact.HonorificSuffix = structuredName.ElementAtOrDefault(4);
        contact.Nickname = TextValue(First(document, "NICKNAME"));
        contact.FileAs = TextValue(First(document, "SORT-STRING"));

        var organization = Components(First(document, "ORG")?.Value, ';');
        contact.CompanyName = organization.ElementAtOrDefault(0);
        contact.Department = organization.ElementAtOrDefault(1);
        contact.JobTitle = TextValue(First(document, "TITLE"));
        contact.Profession = TextValue(First(document, "ROLE"));
        contact.Notes = TextValue(First(document, "NOTE"));
        contact.Website = TextValue(First(document, "URL"));
        ApplyBirthday(contact, TextValue(First(document, "BDAY")));

        var order = 0;
        foreach (var property in All(document, "EMAIL"))
        {
            contact.EmailAddresses.Add(new ContactEmailAddress
            {
                ContactId = contact.Id,
                Address = TextValue(property),
                NormalizedAddress = ContactEmailAddress.Normalize(TextValue(property)),
                Label = GetLabel(property),
                IsPrimary = IsPreferred(property, order),
                Order = order++
            });
        }

        order = 0;
        foreach (var property in All(document, "TEL"))
        {
            contact.PhoneNumbers.Add(new ContactPhoneNumber
            {
                ContactId = contact.Id,
                Number = TextValue(property),
                Kind = GetPhoneKind(property),
                IsPrimary = IsPreferred(property, order),
                Order = order++
            });
        }

        foreach (var property in All(document, "ADR"))
        {
            var parts = Components(property.Value, ';');
            contact.PostalAddresses.Add(new ContactPostalAddress
            {
                ContactId = contact.Id,
                Kind = GetAddressKind(property),
                PostOfficeBox = parts.ElementAtOrDefault(0),
                Street = parts.ElementAtOrDefault(2),
                City = parts.ElementAtOrDefault(3),
                Region = parts.ElementAtOrDefault(4),
                PostalCode = parts.ElementAtOrDefault(5),
                Country = parts.ElementAtOrDefault(6)
            });
        }

        order = 0;
        foreach (var property in All(document, "IMPP"))
        {
            var address = TextValue(property);
            contact.ImAddresses.Add(new ContactImAddress
            {
                ContactId = contact.Id,
                Address = address,
                Protocol = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Scheme : null,
                Order = order++
            });
        }

        order = 0;
        foreach (var property in All(document, "RELATED"))
        {
            var type = ParameterValues(property, "TYPE").FirstOrDefault();
            if (!Enum.TryParse<ContactRelationKind>(type, true, out var kind)) continue;
            contact.Relations.Add(new ContactRelation
            {
                ContactId = contact.Id,
                Name = TextValue(property),
                Kind = kind,
                Order = order++
            });
        }

        return contact;
    }

    public VCardDocument Create(AccountContact contact, string version, string uid = null)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var document = new VCardDocument { Version = version == "4.0" ? "4.0" : "3.0" };
        document.Properties.Add(Property("VERSION", document.Version));
        document.Properties.Add(Property("UID", string.IsNullOrWhiteSpace(uid) ? Guid.NewGuid().ToString("D") : uid));
        Patch(document, contact);
        return document;
    }

    public void Patch(VCardDocument document, AccountContact contact)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(contact);

        PatchSingle(document, "FN", EscapeText(contact.DisplayValue));
        PatchSingle(document, "N", JoinStructured(contact.Surname, contact.GivenName, contact.MiddleName, contact.HonorificPrefix, contact.HonorificSuffix));
        PatchSingle(document, "NICKNAME", EscapeText(contact.Nickname));
        PatchSingle(document, "SORT-STRING", EscapeText(contact.FileAs));
        PatchSingle(document, "ORG", JoinStructured(contact.CompanyName, contact.Department));
        PatchSingle(document, "TITLE", EscapeText(contact.JobTitle));
        PatchSingle(document, "ROLE", EscapeText(contact.Profession));
        PatchSingle(document, "NOTE", EscapeText(contact.Notes));
        PatchSingle(document, "URL", EscapeText(contact.Website));
        PatchSingle(document, "BDAY", FormatBirthday(contact));

        PatchRepeated(document, "EMAIL", contact.EmailAddresses?.OrderBy(item => item.Order).Select(item =>
        {
            var property = Property("EMAIL", EscapeText(item.Address));
            AddType(property, item.Label);
            if (item.IsPrimary) AddPreference(property, document.Version);
            return property;
        }) ?? []);

        PatchRepeated(document, "TEL", contact.PhoneNumbers?.OrderBy(item => item.Order).Select(item =>
        {
            var property = Property("TEL", EscapeText(item.Number));
            AddType(property, item.Kind.ToString());
            if (item.IsPrimary) AddPreference(property, document.Version);
            return property;
        }) ?? []);

        PatchRepeated(document, "ADR", contact.PostalAddresses?.Select(item =>
        {
            var property = Property("ADR", JoinStructured(item.PostOfficeBox, null, item.Street, item.City, item.Region, item.PostalCode, item.Country));
            AddType(property, item.Kind.ToString());
            return property;
        }) ?? []);

        PatchRepeated(document, "IMPP", contact.ImAddresses?.OrderBy(item => item.Order).Select(item =>
            Property("IMPP", EscapeText(item.Address))) ?? []);

        PatchRepeated(document, "RELATED", contact.Relations?.OrderBy(item => item.Order).Select(item =>
        {
            var property = Property("RELATED", EscapeText(item.Name));
            AddType(property, item.Kind.ToString());
            return property;
        }) ?? []);
    }

    public string Serialize(VCardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();
        builder.Append("BEGIN:VCARD\r\n");
        if (!document.Properties.Any(property => property.Name == "VERSION"))
            document.Properties.Insert(0, Property("VERSION", document.Version));

        foreach (var property in document.Properties)
        {
            var line = SerializeProperty(property);
            foreach (var folded in Fold(line))
                builder.Append(folded).Append("\r\n");
        }

        builder.Append("END:VCARD\r\n");
        return builder.ToString();
    }

    public VCardHashes ComputeHashes(VCardDocument document, AccountContact projection, string rawContent = null)
    {
        var serialized = rawContent ?? Serialize(document);
        var semantic = string.Join("\n", document.Properties
            .Where(property => property.Name is not "REV" and not "PRODID")
            .Select(SerializeProperty)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        var domain = string.Join("|",
            projection.DisplayName, projection.HonorificPrefix, projection.GivenName, projection.MiddleName,
            projection.Surname, projection.HonorificSuffix, projection.Nickname, projection.FileAs,
            projection.CompanyName, projection.Department, projection.JobTitle, projection.OfficeLocation,
            projection.Profession, projection.BirthdayYear, projection.BirthdayMonth, projection.BirthdayDay,
            projection.Notes, projection.Website,
            string.Join(",", projection.EmailAddresses.OrderBy(item => item.Order).Select(item => $"{item.Address}:{item.Label}:{item.IsPrimary}")),
            string.Join(",", projection.PhoneNumbers.OrderBy(item => item.Order).Select(item => $"{item.Number}:{item.Kind}:{item.IsPrimary}")),
            string.Join(",", projection.PostalAddresses.Select(item => $"{item.Kind}:{item.PostOfficeBox}:{item.Street}:{item.City}:{item.Region}:{item.PostalCode}:{item.Country}")),
            string.Join(",", projection.ImAddresses.OrderBy(item => item.Order).Select(item => $"{item.Address}:{item.Protocol}")),
            string.Join(",", projection.Relations.OrderBy(item => item.Order).Select(item => $"{item.Name}:{item.Kind}")));
        return new VCardHashes(Hash(serialized), Hash(semantic), Hash(domain));
    }

    private static void PatchSingle(VCardDocument document, string name, string value)
    {
        var existing = All(document, name).ToList();
        if (string.IsNullOrWhiteSpace(value))
        {
            foreach (var property in existing)
                document.Properties.Remove(property);
            return;
        }

        if (existing.Count == 0)
        {
            document.Properties.Add(Property(name, value));
            return;
        }

        existing[0].Value = value;
        foreach (var property in existing.Skip(1))
            document.Properties.Remove(property);
    }

    private static void PatchRepeated(VCardDocument document, string name, IEnumerable<VCardProperty> replacements)
    {
        var existing = All(document, name).ToList();
        var incoming = replacements.Where(property => !string.IsNullOrWhiteSpace(property.Value)).ToList();
        var common = Math.Min(existing.Count, incoming.Count);
        for (var index = 0; index < common; index++)
        {
            // Retain the server's group and unsupported parameters while updating modeled values.
            existing[index].Value = incoming[index].Value;
            MergeModeledParameters(existing[index], incoming[index]);
        }

        foreach (var property in existing.Skip(common))
            document.Properties.Remove(property);
        foreach (var property in incoming.Skip(common))
            document.Properties.Add(property);
    }

    private static void MergeModeledParameters(VCardProperty target, VCardProperty source)
    {
        target.Parameters.RemoveAll(parameter => parameter.Name is "TYPE" or "PREF");
        foreach (var parameter in source.Parameters)
        {
            var copy = new VCardParameter { Name = parameter.Name };
            copy.Values.AddRange(parameter.Values);
            target.Parameters.Add(copy);
        }
    }

    private static IEnumerable<string> Unfold(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && result.Count > 0)
                result[^1] += line[1..];
            else if (result.Count > 0 && result[^1].EndsWith('=') && HasQuotedPrintableEncoding(result[^1]))
                result[^1] = result[^1][..^1] + line;
            else
                result.Add(line);
        }
        return result;
    }

    private static bool HasQuotedPrintableEncoding(string line)
        => line.Contains("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);

    private static string SerializeProperty(VCardProperty property)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(property.Group))
            builder.Append(property.Group).Append('.');
        builder.Append(property.OriginalName ?? property.Name.ToUpperInvariant());
        foreach (var parameter in property.Parameters)
        {
            builder.Append(';').Append(parameter.OriginalName ?? parameter.Name.ToUpperInvariant()).Append('=');
            builder.AppendJoin(',', parameter.Values.Select(EncodeParameter));
        }
        return builder.Append(':').Append(property.Value).ToString();
    }

    private static IEnumerable<string> Fold(string line)
    {
        const int maximumBytes = 75;
        var current = new StringBuilder();
        var currentBytes = 0;
        var continuation = false;
        foreach (var rune in line.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            var limit = continuation ? maximumBytes - 1 : maximumBytes;
            if (currentBytes + runeBytes > limit && current.Length > 0)
            {
                yield return continuation ? " " + current : current.ToString();
                current.Clear();
                currentBytes = 0;
                continuation = true;
            }
            current.Append(rune.ToString());
            currentBytes += runeBytes;
        }
        yield return continuation ? " " + current : current.ToString();
    }

    private static VCardProperty First(VCardDocument document, string name) => All(document, name).FirstOrDefault();
    private static IEnumerable<VCardProperty> All(VCardDocument document, string name) => document.Properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static VCardProperty Property(string name, string value) => new() { Name = name, Value = value ?? string.Empty };
    private static string TextValue(VCardProperty property) => property is null ? null : UnescapeText(DecodeLegacyValue(property));

    private static string DecodeLegacyValue(VCardProperty property)
    {
        if (!property.Parameters.Any(parameter => parameter.Name == "ENCODING" && parameter.Values.Any(value => value.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))))
            return property.Value;

        var bytes = new List<byte>();
        for (var index = 0; index < property.Value.Length; index++)
        {
            if (property.Value[index] == '=' && index + 2 < property.Value.Length && byte.TryParse(property.Value.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                bytes.Add(value);
                index += 2;
            }
            else
            {
                bytes.Add((byte)property.Value[index]);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static List<string> Components(string value, char separator)
        => string.IsNullOrEmpty(value) ? [] : SplitEscaped(value, separator).Select(UnescapeText).ToList();

    private static List<string> SplitEscaped(string value, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append('\\').Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == separator)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        if (escaped) current.Append('\\');
        result.Add(current.ToString());
        return result;
    }

    private static List<string> SplitRespectingQuotes(string value, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                current.Append(character);
                escaped = true;
                continue;
            }
            if (character == '"') quoted = !quoted;
            if (character == separator && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else current.Append(character);
        }
        result.Add(current.ToString());
        return result;
    }

    private static int FindDelimiter(string value, char delimiter)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped) { escaped = false; continue; }
            if (character == '\\') { escaped = true; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (character == delimiter && !quoted) return index;
        }
        return -1;
    }

    private static string EscapeText(string value) => value?.Replace("\\", "\\\\").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string UnescapeText(string value) => value?.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
    private static string JoinStructured(params string[] values) => string.Join(';', values.Select(value => EscapeText(value) ?? string.Empty));
    private static string TrimQuotes(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    private static string DecodeParameter(string value) => value.Replace("^^", "^").Replace("^n", "\n", StringComparison.OrdinalIgnoreCase).Replace("^'", "\"");
    private static string EncodeParameter(string value)
    {
        var encoded = (value ?? string.Empty).Replace("^", "^^").Replace("\n", "^n").Replace("\"", "^'");
        return encoded.IndexOfAny([',', ';', ':']) >= 0 ? $"\"{encoded}\"" : encoded;
    }

    private static void AddType(VCardProperty property, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var parameter = new VCardParameter { Name = "TYPE" };
        parameter.Values.Add(value.ToLowerInvariant());
        property.Parameters.Add(parameter);
    }

    private static void AddPreference(VCardProperty property, string version)
    {
        if (version == "4.0")
        {
            var parameter = new VCardParameter { Name = "PREF" };
            parameter.Values.Add("1");
            property.Parameters.Add(parameter);
        }
        else
        {
            var type = property.Parameters.FirstOrDefault(parameter => parameter.Name == "TYPE") ?? new VCardParameter { Name = "TYPE" };
            if (!property.Parameters.Contains(type)) property.Parameters.Add(type);
            type.Values.Add("pref");
        }
    }

    private static string GetLabel(VCardProperty property)
        => property.Parameters.FirstOrDefault(parameter => parameter.Name == "TYPE")?.Values.FirstOrDefault(value => !value.Equals("pref", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ParameterValues(VCardProperty property, string name)
        => property.Parameters.Where(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(parameter => parameter.Values);

    private static bool IsPreferred(VCardProperty property, int order)
        => property.Parameters.Any(parameter => parameter.Name == "PREF" && parameter.Values.Contains("1")) ||
           property.Parameters.Any(parameter => parameter.Name == "TYPE" && parameter.Values.Contains("pref", StringComparer.OrdinalIgnoreCase)) || order == 0;

    private static ContactPhoneKind GetPhoneKind(VCardProperty property)
    {
        var types = property.Parameters.Where(parameter => parameter.Name == "TYPE").SelectMany(parameter => parameter.Values).ToList();
        if (types.Contains("cell", StringComparer.OrdinalIgnoreCase) || types.Contains("mobile", StringComparer.OrdinalIgnoreCase)) return ContactPhoneKind.Mobile;
        if (types.Contains("work", StringComparer.OrdinalIgnoreCase)) return ContactPhoneKind.Work;
        return ContactPhoneKind.Home;
    }

    private static ContactPostalAddressKind GetAddressKind(VCardProperty property)
    {
        var types = property.Parameters.Where(parameter => parameter.Name == "TYPE").SelectMany(parameter => parameter.Values).ToList();
        if (types.Contains("work", StringComparer.OrdinalIgnoreCase)) return ContactPostalAddressKind.Business;
        if (types.Contains("home", StringComparer.OrdinalIgnoreCase)) return ContactPostalAddressKind.Home;
        return ContactPostalAddressKind.Other;
    }

    private static string FormatBirthday(AccountContact contact)
    {
        if (!contact.BirthdayMonth.HasValue || !contact.BirthdayDay.HasValue) return null;
        return contact.BirthdayYear.HasValue
            ? $"{contact.BirthdayYear:0000}-{contact.BirthdayMonth:00}-{contact.BirthdayDay:00}"
            : $"--{contact.BirthdayMonth:00}{contact.BirthdayDay:00}";
    }

    private static void ApplyBirthday(AccountContact contact, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            contact.BirthdayYear = date.Year;
            contact.BirthdayMonth = date.Month;
            contact.BirthdayDay = date.Day;
            return;
        }
        var partial = value.Trim().TrimStart('-').Replace("-", string.Empty);
        if (partial.Length == 4 && int.TryParse(partial[..2], out var month) && int.TryParse(partial[2..], out var day))
        {
            contact.BirthdayMonth = month;
            contact.BirthdayDay = day;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));
}
