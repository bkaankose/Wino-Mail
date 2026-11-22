namespace Wino.Editor.WebKitGtk;

/// <summary>
/// WebKitGTK spell checking through Enchant. The settings belong to the default web context, so they apply to every
/// editor at once: the last call wins, which matches the single composer preference all editors share.
/// </summary>
public static class WebKitGtkSpellCheck
{
    /// <summary>Turns checking on or off; an empty code checks in the language of the user interface.</summary>
    public static void Configure(bool enabled, string? languageCode)
    {
        var context = WebKit.WebContext.GetDefault();
        context.SetSpellCheckingEnabled(enabled);
        if (!enabled) return;
        var code = string.IsNullOrWhiteSpace(languageCode) ? System.Globalization.CultureInfo.CurrentUICulture.Name : languageCode;
        context.SetSpellCheckingLanguages([ToEnchant(string.IsNullOrWhiteSpace(code) ? "en-US" : code)]);
    }

    /// <summary>"en-US" to Enchant's "en_US".</summary>
    public static string ToEnchant(string languageCode) => languageCode.Trim().Replace('-', '_');

    /// <summary>The languages Enchant really checks; a language without an installed dictionary is missing here.</summary>
    public static string[] EffectiveLanguages() => WebKit.WebContext.GetDefault().GetSpellCheckingLanguages() ?? [];

    public static bool IsEnabled => WebKit.WebContext.GetDefault().GetSpellCheckingEnabled();
}
