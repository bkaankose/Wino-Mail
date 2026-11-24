using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Serilog;

namespace Wino.Mail.Rendering;

/// <summary>A language the composer can check spelling in: the tag the engine takes and the name the menu shows.</summary>
public sealed record SpellCheckLanguageOption(string Code, string DisplayName);

/// <summary>
/// The spell-check languages installed on the system, as the mail engine's spell checker reports them. They are not
/// the app languages: the system may check a language Wino is not translated into, and lack one it is.
/// </summary>
public static class EngineSpellCheckLanguages
{
    /// <summary>Language tags the engine can check. Empty when the engine or the system spell checker is unavailable.</summary>
    public static IReadOnlyList<string> GetInstalledCodes(MailEngineHost engineHost)
    {
        try
        {
            return engineHost.Backend.SpellChecker?.GetLanguages() ?? [];
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "The installed spell-check languages could not be read.");
            return [];
        }
    }

    public static SpellCheckLanguageOption[] GetInstalled(MailEngineHost engineHost)
        => GetInstalledCodes(engineHost)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => new SpellCheckLanguageOption(code, GetDisplayName(code)))
            .OrderBy(language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    /// <summary>
    /// The installed language a saved code stands for: the language itself, else another region of it. Null when
    /// the system has no checker for the language at all; the engine then checks in the system language.
    /// </summary>
    public static string? Resolve(string? savedCode, IReadOnlyList<string> installedCodes)
    {
        if (string.IsNullOrWhiteSpace(savedCode))
            return null;

        var code = savedCode.Trim().Replace('_', '-');

        if (installedCodes.FirstOrDefault(installed => string.Equals(installed, code, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        var language = PrimaryLanguage(code);

        return installedCodes.FirstOrDefault(installed => string.Equals(PrimaryLanguage(installed), language, StringComparison.OrdinalIgnoreCase));
    }

    public static string? Resolve(string? savedCode, MailEngineHost engineHost) => Resolve(savedCode, GetInstalledCodes(engineHost));

    private static string PrimaryLanguage(string code)
    {
        var separator = code.IndexOf('-', StringComparison.Ordinal);
        return separator < 0 ? code : code[..separator];
    }

    private static string GetDisplayName(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).DisplayName;
            return string.IsNullOrWhiteSpace(name) ? code : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}

/// <summary>The Settings page lists the same installed languages as the composer's language menu.</summary>
public sealed class EngineSpellCheckLanguageSource(MailEngineHost engineHost) : Wino.Mail.ViewModels.ISpellCheckLanguageSource
{
    public IReadOnlyList<(string Code, string DisplayName)> GetLanguages()
        => EngineSpellCheckLanguages.GetInstalled(engineHost).Select(language => (language.Code, language.DisplayName)).ToArray();

    public string? Resolve(string? savedCode) => EngineSpellCheckLanguages.Resolve(savedCode, engineHost);
}
