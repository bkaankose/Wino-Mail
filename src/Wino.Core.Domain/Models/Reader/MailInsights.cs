#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// A value of the structured data a message carries (schema.org JSON-LD or microdata), as a generic tree. The reader
/// reads it from the sender's markup only; nothing in it is verified.
/// </summary>
public abstract record MailStructuredValue;

public sealed record MailStructuredText(string Value) : MailStructuredValue;

public sealed record MailStructuredNumber(double Value) : MailStructuredValue;

public sealed record MailStructuredBoolean(bool Value) : MailStructuredValue;

public sealed record MailStructuredList(IReadOnlyList<MailStructuredValue> Values) : MailStructuredValue;

/// <summary>An item with its schema.org type name ("FlightReservation"), empty when it declares none. Properties keep the sender's order.</summary>
public sealed record MailStructuredItem(string Type, IReadOnlyList<MailStructuredProperty> Properties) : MailStructuredValue;

public sealed record MailStructuredProperty(string Name, MailStructuredValue Value);

/// <summary>
/// A date found in the shown text. <paramref name="Index"/> identifies it within the analysis that found it.
/// Without a year in the text, <paramref name="HasYear"/> is false and the year is taken from the message's date.
/// </summary>
public sealed record MailDetectedDate(int Index, DateOnly Date, TimeOnly? Time, bool HasYear, string Text);

/// <summary>What the reader found in a shown message after it was drawn.</summary>
public sealed record MailReaderInsights(IReadOnlyList<MailStructuredItem> StructuredData, IReadOnlyList<MailDetectedDate> Dates)
{
    public static MailReaderInsights Empty { get; } = new(Array.Empty<MailStructuredItem>(), Array.Empty<MailDetectedDate>());
}
