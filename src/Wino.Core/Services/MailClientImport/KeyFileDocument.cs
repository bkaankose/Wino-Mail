#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wino.Core.Services.MailClientImport;

/// <summary>
/// An INI-style file: Mozilla's profiles.ini and the GLib key files of Evolution and GNOME Online Accounts. Section and
/// key names compare without case; a localized key such as <c>Name[de]</c> is kept under its full name.
/// </summary>
internal sealed class KeyFileDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public List<string> SectionNames { get; } = new();

    /// <param name="glibEscapes">GLib key files escape with backslashes; Mozilla's profiles.ini holds Windows paths.</param>
    public static KeyFileDocument? TryLoad(string path, bool glibEscapes)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path), glibEscapes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static KeyFileDocument Parse(string content, bool glibEscapes)
    {
        var document = new KeyFileDocument();
        Dictionary<string, string>? current = null;

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
                continue;

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                var name = trimmed[1..^1].Trim();
                if (!document._sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    document._sections[name] = current;
                    document.SectionNames.Add(name);
                }

                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (current == null || separator <= 0)
                continue;

            var value = trimmed[(separator + 1)..].Trim();
            current[trimmed[..separator].Trim()] = glibEscapes ? Unescape(value) : value;
        }

        return document;
    }

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public string Get(string section, string key)
        => _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : string.Empty;

    public int? GetInt(string section, string key)
        => int.TryParse(Get(section, key), out var value) ? value : null;

    public bool? GetBool(string section, string key)
        => Get(section, key).ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null
        };

    // GLib escapes: \s space, \n, \t, \r and \\.
    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i == value.Length - 1)
            {
                builder.Append(value[i]);
                continue;
            }

            var next = value[++i];
            builder.Append(next switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => next
            });
        }

        return builder.ToString();
    }
}
