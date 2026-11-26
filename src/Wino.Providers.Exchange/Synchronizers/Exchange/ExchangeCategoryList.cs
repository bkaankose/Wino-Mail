using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>One category of the mailbox's master category list.</summary>
public sealed record ExchangeCategoryEntry(string Name, int Color, string? Guid);

/// <summary>
/// The mailbox's master category list: the XML document (MS-OXOCFG 2.2.5.2.2) in the "CategoryList" configuration
/// message of the Calendar folder, the same document over EWS (user configuration XmlData) and MAPI
/// (PidTagRoamingXmlStream). Edits change the parsed document in place, so elements and attributes this client does
/// not know are written back as they were read (MS-OXOCFG 3.1.4.2.3).
/// </summary>
public sealed class ExchangeCategoryList
{
    private const string DefaultNamespace = "CategoryList.xsd";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff";

    private readonly XDocument _document;
    private readonly XNamespace _namespace;

    private ExchangeCategoryList(XDocument document)
    {
        _document = document;
        _namespace = document.Root?.Name.Namespace ?? XNamespace.None;
    }

    private XElement Root => _document.Root ?? throw new InvalidOperationException("The category list has no root element.");

    public static ExchangeCategoryList Parse(byte[] xml)
    {
        using var stream = new MemoryStream(xml, writable: false);
        var document = XDocument.Load(stream, LoadOptions.None);
        if (document.Root is not { } root || root.Name.LocalName != "categories")
            throw new FormatException("The category list document has no categories element (MS-OXOCFG 2.2.5.2.2).");

        return new ExchangeCategoryList(document);
    }

    /// <summary>A list for a mailbox that has none yet, with the attributes the schema requires on the root.</summary>
    public static ExchangeCategoryList CreateEmpty()
    {
        XNamespace ns = DefaultNamespace;
        var document = new XDocument(
            new XDeclaration("1.0", null, null),
            new XElement(ns + "categories",
                new XAttribute("default", string.Empty),
                new XAttribute("lastSavedSession", "0"),
                new XAttribute("lastSavedTime", FormatTime(DateTime.UtcNow))));

        return new ExchangeCategoryList(document);
    }

    public IReadOnlyList<ExchangeCategoryEntry> Categories
        => [.. CategoryElements()
            .Select(element => new ExchangeCategoryEntry(
                element.Attribute("name")?.Value.Trim() ?? string.Empty,
                ParseColor(element.Attribute("color")?.Value),
                element.Attribute("guid")?.Value))
            .Where(entry => entry.Name.Length > 0)];

    /// <summary>
    /// Adds a category, or recolours the one that already carries the name (names are unique, case-insensitive).
    /// Returns its guid.
    /// </summary>
    public string Add(string name, int color)
    {
        ValidateName(name);

        if (Find(guid: null, name) is { } existing)
            return Update(existing, name, color);

        var guid = "{" + System.Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
        var now = FormatTime(DateTime.UtcNow);
        Root.Add(new XElement(_namespace + "category",
            new XAttribute("name", name),
            new XAttribute("color", color.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("keyboardShortcut", "0"),
            new XAttribute("usageCount", "0"),
            new XAttribute("lastTimeUsed", now),
            new XAttribute("lastSessionUsed", "0"),
            new XAttribute("guid", guid)));

        return guid;
    }

    /// <summary>
    /// Renames or recolours the category found by its guid, or by its previous name when the guid is unknown, and adds
    /// it when the list does not have it. Returns its guid.
    /// </summary>
    public string Upsert(string? previousGuid, string? previousName, string name, int color)
    {
        ValidateName(name);

        var element = Find(previousGuid, previousName);
        if (element is null)
            return Add(name, color);

        if (Find(guid: null, name) is { } clash && clash != element)
            throw new InvalidOperationException($"The Exchange category list already has a category named '{name}'.");

        return Update(element, name, color);
    }

    /// <summary>Removes the category; false when the list does not have it.</summary>
    public bool Remove(string? guid, string? name)
    {
        var element = Find(guid, name);
        if (element is null)
            return false;

        if (string.Equals(Root.Attribute("default")?.Value, element.Attribute("name")?.Value, StringComparison.OrdinalIgnoreCase))
            Root.SetAttributeValue("default", string.Empty);

        element.Remove();
        return true;
    }

    /// <summary>
    /// The schema requires at least one category element (MS-OXOCFG 2.2.5.2.2), so a list without one is not written:
    /// its configuration message is removed and the mailbox is back to having no list.
    /// </summary>
    public bool IsEmpty => !CategoryElements().Any();

    /// <summary>The colour the list stores for a category, when it has the category.</summary>
    public int? ColorOf(string? guid, string? name)
        => Find(guid, name) is { } element ? ParseColor(element.Attribute("color")?.Value) : null;

    public byte[] Serialize()
    {
        Root.SetAttributeValue("lastSavedTime", FormatTime(DateTime.UtcNow));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            _document.Save(writer);

        return stream.ToArray();
    }

    private string Update(XElement element, string name, int color)
    {
        var previousName = element.Attribute("name")?.Value;
        var previousColor = ParseColor(element.Attribute("color")?.Value);
        if (!string.Equals(previousName, name, StringComparison.Ordinal) || previousColor != color)
        {
            if (string.Equals(Root.Attribute("default")?.Value, previousName, StringComparison.OrdinalIgnoreCase))
                Root.SetAttributeValue("default", name);

            element.SetAttributeValue("name", name);
            element.SetAttributeValue("color", color.ToString(CultureInfo.InvariantCulture));

            // MS-OXOCFG 3.1.4.2.3: a change to the user-visible properties updates lastTimeUsed.
            element.SetAttributeValue("lastTimeUsed", FormatTime(DateTime.UtcNow));
        }

        if (element.Attribute("guid")?.Value is { Length: > 0 } guid)
            return guid;

        guid = "{" + System.Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
        element.SetAttributeValue("guid", guid);
        return guid;
    }

    private IEnumerable<XElement> CategoryElements() => Root.Elements().Where(element => element.Name.LocalName == "category");

    private XElement? Find(string? guid, string? name)
    {
        if (!string.IsNullOrWhiteSpace(guid)
            && CategoryElements().FirstOrDefault(element => string.Equals(element.Attribute("guid")?.Value, guid, StringComparison.OrdinalIgnoreCase)) is { } byGuid)
        {
            return byGuid;
        }

        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : CategoryElements().FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    // MS-OXOCFG 2.2.5.2.2: a value outside -1..24 is read as -1 (no colour).
    private static int ParseColor(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var color) && color is >= -1 and <= 24 ? color : -1;

    private static void ValidateName(string name)
    {
        // MS-OXOCFG 2.2.5.2.2: not empty, at most 255 characters, no comma.
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Contains(','))
            throw new InvalidOperationException($"'{name}' is not a valid Exchange category name: it must have 1 to 255 characters and no comma.");
    }

    private static string FormatTime(DateTime utc) => utc.ToString(TimeFormat, CultureInfo.InvariantCulture);
}

/// <summary>
/// Category colours. MS-OXOCFG colour N is the colour Microsoft Graph calls presetN, so N maps to the same Wino colour
/// pair the Outlook provider uses for presetN; -1 (no colour) maps to the pair Outlook uses for a category without one.
/// </summary>
public static class ExchangeCategoryColors
{
    public const int NoColor = -1;

    private static readonly MailCategoryColorOption NoColorOption = new("#E5E7EB", "#374151");

    private static readonly MailCategoryColorOption[] Presets =
    [
        new("#FEE2E2", "#991B1B"),
        new("#FFEDD5", "#9A3412"),
        new("#FEF3C7", "#92400E"),
        new("#ECFCCB", "#3F6212"),
        new("#DCFCE7", "#166534"),
        new("#CCFBF1", "#115E59"),
        new("#CFFAFE", "#155E75"),
        new("#DBEAFE", "#1D4ED8"),
        new("#E0E7FF", "#4338CA"),
        new("#F3E8FF", "#7E22CE"),
        new("#FCE7F3", "#9D174D"),
        new("#FECACA", "#7F1D1D"),
        new("#FED7AA", "#7C2D12"),
        new("#FDE68A", "#78350F"),
        new("#D9F99D", "#365314"),
        new("#BBF7D0", "#14532D"),
        new("#99F6E4", "#134E4A"),
        new("#A5F3FC", "#164E63"),
        new("#BFDBFE", "#1E3A8A"),
        new("#DDD6FE", "#5B21B6"),
        new("#E5E7EB", "#374151"),
        new("#D1D5DB", "#1F2937"),
        new("#F3F4F6", "#111827"),
        new("#E2E8F0", "#334155"),
        new("#F8FAFC", "#475569"),
    ];

    public static MailCategoryColorOption ToOption(int color)
        => color >= 0 && color < Presets.Length ? Presets[color] : NoColorOption;

    /// <summary>
    /// The list colour for a Wino category. The colour the list already stores is kept when it shows as the same pair
    /// (no colour and preset 20 look alike); a pair that is no preset is written as no colour.
    /// </summary>
    public static int ToColor(MailCategory category, int? storedColor)
    {
        var background = category.BackgroundColorHex?.ToUpperInvariant();
        var text = category.TextColorHex?.ToUpperInvariant();

        if (storedColor is { } stored && Matches(ToOption(stored), background, text))
            return stored;

        if (Matches(NoColorOption, background, text))
            return NoColor;

        var index = Array.FindIndex(Presets, preset => Matches(preset, background, text));
        return index >= 0 ? index : NoColor;
    }

    private static bool Matches(MailCategoryColorOption option, string? background, string? text)
        => option.BackgroundColorHex == background && option.TextColorHex == text;
}
