#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wino.Core.Domain.Enums;

namespace Wino.Services;

/// <summary>One certificate the application store manages, by SHA-1 thumbprint and purpose.</summary>
public sealed record SmimeManagedThumbprint(string Thumbprint, SmimeCertificatePurpose Purpose);

/// <summary>
/// The index of certificates an application-owned S/MIME store holds (macOS keeps them in its own
/// encrypted store, invisible to Keychain Access). One certificate can be listed for both purposes;
/// removing it from one purpose keeps the other. Thumbprints are normalized to upper-case hex so the
/// values match <c>X509Certificate2.Thumbprint</c>. The text form is one "Purpose THUMBPRINT" line per
/// entry under a version header, and parsing skips anything it does not understand.
/// </summary>
public sealed class SmimeManagedThumbprintList
{
    private const string Header = "wino-smime-managed v1";
    private readonly List<SmimeManagedThumbprint> _entries = [];

    public IReadOnlyList<SmimeManagedThumbprint> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>Strips spaces, colons and dashes and upper-cases the hex digits; throws for anything that is not hex.</summary>
    public static string Normalize(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) throw new ArgumentException("A thumbprint is required.", nameof(thumbprint));
        var builder = new StringBuilder(thumbprint.Length);
        foreach (var character in thumbprint)
        {
            if (char.IsWhiteSpace(character) || character is ':' or '-') continue;
            if (!Uri.IsHexDigit(character)) throw new ArgumentException("A thumbprint contains only hexadecimal digits.", nameof(thumbprint));
            builder.Append(char.ToUpperInvariant(character));
        }
        if (builder.Length == 0 || builder.Length % 2 != 0) throw new ArgumentException("A thumbprint has an even number of hexadecimal digits.", nameof(thumbprint));
        return builder.ToString();
    }

    /// <summary>Normalizes like <see cref="Normalize"/>, returning false instead of throwing.</summary>
    public static bool TryNormalize(string? thumbprint, out string normalized)
    {
        try
        {
            normalized = Normalize(thumbprint!);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    /// <summary>Adds the entry; false when it is already listed for that purpose.</summary>
    public bool Add(string thumbprint, SmimeCertificatePurpose purpose)
    {
        var entry = new SmimeManagedThumbprint(Normalize(thumbprint), purpose);
        if (_entries.Contains(entry)) return false;
        _entries.Add(entry);
        return true;
    }

    /// <summary>Removes the entry for that purpose only; false when it was not listed.</summary>
    public bool Remove(string thumbprint, SmimeCertificatePurpose purpose)
        => TryNormalize(thumbprint, out var normalized) && _entries.Remove(new SmimeManagedThumbprint(normalized, purpose));

    public bool Contains(string thumbprint, SmimeCertificatePurpose purpose)
        => TryNormalize(thumbprint, out var normalized) && _entries.Contains(new SmimeManagedThumbprint(normalized, purpose));

    /// <summary>True while any purpose still lists the thumbprint.</summary>
    public bool IsReferenced(string thumbprint)
        => TryNormalize(thumbprint, out var normalized) && _entries.Any(entry => entry.Thumbprint == normalized);

    /// <summary>Thumbprints for one purpose, in the order they were added.</summary>
    public IReadOnlyList<string> GetThumbprints(SmimeCertificatePurpose purpose)
        => _entries.Where(entry => entry.Purpose == purpose).Select(entry => entry.Thumbprint).ToList();

    public string Serialize()
    {
        var builder = new StringBuilder();
        builder.Append(Header).Append('\n');
        foreach (var entry in _entries)
            builder.Append(entry.Purpose).Append(' ').Append(entry.Thumbprint).Append('\n');
        return builder.ToString();
    }

    /// <summary>Reads the text form. Null, empty or unknown content yields an empty list; bad lines are skipped.</summary>
    public static SmimeManagedThumbprintList Parse(string? text)
    {
        var list = new SmimeManagedThumbprintList();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header) return list;

        foreach (var raw in lines.Skip(1))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !char.IsLetter(parts[0][0])) continue;
            if (!Enum.TryParse<SmimeCertificatePurpose>(parts[0], ignoreCase: false, out var purpose) || !Enum.IsDefined(purpose)) continue;
            if (!TryNormalize(parts[1], out var thumbprint)) continue;
            list.Add(thumbprint, purpose);
        }
        return list;
    }
}
