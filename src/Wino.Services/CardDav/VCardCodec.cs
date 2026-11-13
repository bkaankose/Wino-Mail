using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using SkiaSharp;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.CardDav;

public sealed partial class VCardCodec : IVCardCodec
{
    private const string AppleKind = "X-ADDRESSBOOKSERVER-KIND";
    private const string AppleMember = "X-ADDRESSBOOKSERVER-MEMBER";
    private const int MaximumPhotoBytes = 8 * 1024 * 1024;

    // The content line each parsed property came from, and how it serializes unchanged. A property
    // that still serializes the same way is written back as the server sent it.
    private static readonly ConditionalWeakTable<VCardProperty, ParsedLine> ParsedLines = new();

    private sealed record ParsedLine(string Raw, string Canonical);

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

            ParsedLines.AddOrUpdate(property, new ParsedLine(line, SerializeProperty(property)));
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

        // FN is mandatory but not every card carries one. The structured name stands in.
        if (string.IsNullOrWhiteSpace(contact.DisplayName))
            contact.DisplayName = contact.StructuredName;

        contact.Nickname = TextValue(First(document, "NICKNAME"));
        contact.FileAs = TextValue(First(document, "SORT-STRING"));

        var organization = Components(First(document, "ORG")?.Value, ';');
        contact.CompanyName = organization.ElementAtOrDefault(0);
        contact.Department = organization.ElementAtOrDefault(1);
        contact.JobTitle = TextValue(First(document, "TITLE"));
        contact.Profession = TextValue(First(document, "ROLE"));
        contact.Notes = TextValue(First(document, "NOTE"));
        contact.Website = TextValue(First(document, "URL"));
        contact.CategoryNames = [.. GetCategories(document)];
        ApplyBirthday(contact, TextValue(First(document, "BDAY")));

        // Apple stores a birthday without a year as a full date in a placeholder year.
        if (First(document, "BDAY")?.Parameters.Any(parameter => parameter.Name == "X-APPLE-OMIT-YEAR") == true)
            contact.BirthdayYear = null;

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
            if (ParsedLines.TryGetValue(property, out var parsed) && string.Equals(parsed.Canonical, line, StringComparison.Ordinal))
                line = parsed.Raw;

            foreach (var folded in Fold(line))
                builder.Append(folded).Append("\r\n");
        }

        builder.Append("END:VCARD\r\n");
        return builder.ToString();
    }

    public string GetUid(VCardDocument document) => TextValue(First(document, "UID"))?.Trim();

    public void SetPhoto(VCardDocument document, byte[] photo)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(photo);

        RemovePhoto(document);
        // Radicale escapes the ';' of a vCard 4 data: URI and corrupts it; ENCODING=b survives in both versions.
        var subtype = GetImageSubtype(photo);
        var property = Property("PHOTO", Convert.ToBase64String(photo));
        var encoding = new VCardParameter { Name = "ENCODING" };
        encoding.Values.Add("b");
        var type = new VCardParameter { Name = "TYPE" };
        type.Values.Add(subtype.ToUpperInvariant());
        property.Parameters.Add(encoding);
        property.Parameters.Add(type);
        document.Properties.Add(property);
    }

    /// <summary>
    /// The card's inline photo: base64 with ENCODING=b in vCard 3 (RFC 2426 section 3.1.4) or a
    /// data: URI in vCard 4 (RFC 6350 section 6.2.4, RFC 2397). A photo given by URL is not
    /// downloaded, and anything that is not a readable image of at most 8 MB is ignored.
    /// </summary>
    public byte[] GetPhoto(VCardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var property = First(document, "PHOTO");
        if (property is null || string.IsNullOrWhiteSpace(property.Value))
            return null;

        string base64 = null;
        var value = property.Value.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // Some servers escape the ';' and ',' of the URI as if it were text.
            var uri = value.Replace("\\;", ";").Replace("\\,", ",").Replace("\\:", ":");
            var comma = uri.IndexOf(',');
            if (comma > 0 && uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                base64 = uri[(comma + 1)..];
        }
        else if (property.Parameters.Any(parameter => parameter.Name == "ENCODING" &&
                 parameter.Values.Any(item => item.Equals("b", StringComparison.OrdinalIgnoreCase) || item.Equals("BASE64", StringComparison.OrdinalIgnoreCase))))
        {
            base64 = value;
        }

        if (string.IsNullOrEmpty(base64) || base64.Length > MaximumPhotoBytes / 3 * 4 + 4096)
            return null;

        try
        {
            var bytes = Convert.FromBase64String(new string(base64.Where(character => !char.IsWhiteSpace(character)).ToArray()));
            if (bytes.Length == 0 || bytes.Length > MaximumPhotoBytes)
                return null;

            var bounds = SKBitmap.DecodeBounds(bytes);
            return bounds.Width > 0 && bounds.Height > 0 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void RemovePhoto(VCardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Properties.RemoveAll(property => property.Name.Equals("PHOTO", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetImageSubtype(byte[] photo)
    {
        if (photo.Length >= 8 && photo[0] == 0x89 && photo[1] == 0x50 && photo[2] == 0x4E && photo[3] == 0x47)
            return "png";
        if (photo.Length >= 6 && photo[0] == 0x47 && photo[1] == 0x49 && photo[2] == 0x46)
            return "gif";
        return "jpeg";
    }

    public IReadOnlyList<string> GetCategories(VCardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return All(document, "CATEGORIES")
            .SelectMany(property => Components(property.Value, ','))
            .Select(name => name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetCategories(VCardDocument document, IEnumerable<string> categoryNames)
    {
        ArgumentNullException.ThrowIfNull(document);

        var names = (categoryNames ?? [])
            .Select(name => name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        document.Properties.RemoveAll(property => property.Name.Equals("CATEGORIES", StringComparison.OrdinalIgnoreCase));
        if (names.Count > 0)
            document.Properties.Add(Property("CATEGORIES", string.Join(",", names.Select(EscapeText))));
    }

    public VCardDocument CreateGroup(string name, string version, string uid)
    {
        var document = new VCardDocument { Version = version == "4.0" ? "4.0" : "3.0" };
        document.Properties.Add(Property("VERSION", document.Version));
        document.Properties.Add(Property("UID", uid));

        // vCard 3 has no group kind of its own; servers of that generation use Apple's extension.
        document.Properties.Add(Property(document.Version == "4.0" ? "KIND" : AppleKind, "group"));
        SetGroupName(document, name);
        return document;
    }

    public bool IsGroup(VCardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var kind = First(document, AppleKind) ?? First(document, "KIND");
        return string.Equals(kind?.Value?.Trim(), "group", StringComparison.OrdinalIgnoreCase);
    }

    public string GetGroupName(VCardDocument document)
    {
        var name = TextValue(First(document, "FN"));
        return string.IsNullOrWhiteSpace(name) ? Components(First(document, "N")?.Value, ';').FirstOrDefault() : name;
    }

    public void SetGroupName(VCardDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        var fullName = First(document, "FN");
        if (fullName is null) Insert(document, Property("FN", EscapeText(name)));
        else SetValue(fullName, EscapeText(name));

        var structured = First(document, "N");
        if (structured is null) Insert(document, Property("N", JoinStructured(name, null, null, null, null)));
        else ReplaceComponents(structured, 5, (0, null, name ?? string.Empty));
    }

    public IReadOnlyList<string> GetGroupMembers(VCardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return All(document, AppleMember).Concat(All(document, "MEMBER"))
            .Select(property => MemberUid(property.Value))
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Makes the group name exactly the given members. A member the group already names keeps its
    /// line as the server has it; a new one is added in the group's own member property.
    /// </summary>
    public void SetGroupMembers(VCardDocument document, IEnumerable<string> memberUids)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Keep the member property the group already uses: Apple's extension or RFC 6350.
        var name = All(document, "MEMBER").Any() || (First(document, AppleKind) is null && First(document, "KIND") is not null)
            ? "MEMBER"
            : AppleMember;
        var desired = (memberUids ?? [])
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .GroupBy(VCardUid.Normalize, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var wanted = desired.Select(VCardUid.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        document.Properties.RemoveAll(property =>
        {
            if (property.Name is not (AppleMember or "MEMBER")) return false;
            var uid = MemberUid(property.Value);
            return !wanted.Contains(uid) || !kept.Add(uid);
        });

        foreach (var uid in desired.Where(uid => !kept.Contains(VCardUid.Normalize(uid))))
            Insert(document, Property(name, VCardUid.ToMemberReference(uid)));
    }

    private static string MemberUid(string value) => VCardUid.Normalize(value);

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
    private static string UnescapeText(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\\')) return value;

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 == value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            var escaped = value[++index];
            builder.Append(escaped is 'n' or 'N' ? '\n' : escaped);
        }
        return builder.ToString();
    }

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
        => property.Parameters.Where(parameter => parameter.Name == "TYPE").SelectMany(parameter => parameter.Values).FirstOrDefault(value =>
            !value.Equals("pref", StringComparison.OrdinalIgnoreCase) && !value.Equals("internet", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>A date in the form of the card's version: RFC 6350 section 4.3.1 basic format, or RFC 2426 section 3.1.5.</summary>
    private static string FormatBirthday(AccountContact contact, string version)
    {
        if (!contact.BirthdayMonth.HasValue || !contact.BirthdayDay.HasValue) return null;
        if (!contact.BirthdayYear.HasValue) return $"--{contact.BirthdayMonth:00}{contact.BirthdayDay:00}";
        return version == "4.0"
            ? $"{contact.BirthdayYear:0000}{contact.BirthdayMonth:00}{contact.BirthdayDay:00}"
            : $"{contact.BirthdayYear:0000}-{contact.BirthdayMonth:00}-{contact.BirthdayDay:00}";
    }

    /// <summary>
    /// Reads a date or date-time BDAY in the basic or extended format (RFC 6350 section 4.3,
    /// RFC 2426 section 3.1.5), with or without a year. Only the date part is used.
    /// </summary>
    private static void ApplyBirthday(AccountContact contact, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        var timeSeparator = trimmed.IndexOf('T', StringComparison.OrdinalIgnoreCase);
        var datePart = timeSeparator > 0 ? trimmed[..timeSeparator] : trimmed;

        if (DateOnly.TryParseExact(datePart, ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) ||
            DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out exact))
        {
            contact.BirthdayYear = exact.Year;
            contact.BirthdayMonth = exact.Month;
            contact.BirthdayDay = exact.Day;
            return;
        }

        var partial = datePart.TrimStart('-').Replace("-", string.Empty);
        if (partial.Length == 4 && int.TryParse(partial[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
            int.TryParse(partial[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var day) && month is >= 1 and <= 12 && day is >= 1 and <= 31)
        {
            contact.BirthdayMonth = month;
            contact.BirthdayDay = day;
        }
    }
}
