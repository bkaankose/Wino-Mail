using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Rendering;

/// <summary>The font families the engine's text backend can draw.</summary>
public sealed class EngineMailFontSource(MailEngineHost engineHost) : IMailFontSource
{
    public IReadOnlyList<string> GetFontFamilies()
    {
        try
        {
            return engineHost.Backend.Text.GetSystemFontFamilies()
                .Where(family => !string.IsNullOrWhiteSpace(family))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(family => family, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "The font families of the mail engine could not be read.");
            return [];
        }
    }
}
