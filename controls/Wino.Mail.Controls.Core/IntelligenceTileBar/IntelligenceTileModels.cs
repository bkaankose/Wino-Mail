#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core.IntelligenceTileBar;

/// <summary>Identifies the semantic kind and fixed ordering group of a mail intelligence tile.</summary>
public enum WinoIntelligenceTileKind
{
    Deadline,
    NeedsReply,
    Priority,
    SmartLabel,
    BriefingFact,
}

#if WINRT_EXPOSED
[GeneratedBindableCustomProperty]
#endif
/// <summary>Describes one localized, noninteractive intelligence indicator.</summary>
public sealed partial class WinoIntelligenceTile
{
    public WinoIntelligenceTile(
        WinoIntelligenceTileKind kind,
        string glyph,
        string text,
        string accessibleText,
        bool isAlwaysIconOnly = false,
        bool isWarning = false)
    {
        Kind = kind;
        Glyph = glyph;
        Text = text;
        AccessibleText = accessibleText;
        IsAlwaysIconOnly = isAlwaysIconOnly;
        IsWarning = isWarning;
    }

    /// <summary>Gets or sets the semantic tile kind.</summary>
    public WinoIntelligenceTileKind Kind { get; set; }

    /// <summary>Gets or sets the Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; set; } = string.Empty;

    /// <summary>Gets or sets the localized detailed label.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the localized tooltip and automation name.</summary>
    public string AccessibleText { get; set; } = string.Empty;

    /// <summary>Gets or sets whether detailed rows must still render this tile as an icon.</summary>
    public bool IsAlwaysIconOnly { get; set; }

    /// <summary>Gets or sets whether the tile uses warning emphasis.</summary>
    public bool IsWarning { get; set; }

}
