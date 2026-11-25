using System.Collections.Generic;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The font families the mail renderer can draw, for the reader and composer font settings. A head without the
/// renderer registers none, and the settings fall back to the fonts the system reports.
/// </summary>
public interface IMailFontSource
{
    /// <summary>Family names in display order. Empty when the renderer reports none.</summary>
    IReadOnlyList<string> GetFontFamilies();
}
