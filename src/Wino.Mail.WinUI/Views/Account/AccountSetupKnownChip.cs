using Wino.Core.Domain.Enums;

namespace Wino.Views;

/// <summary>One item of the known-so-far card as the page draws it: the text, its mark and the separator before it.</summary>
public sealed partial class AccountSetupKnownChip
{
    public AccountSetupKnownChip(string text, WinoIconGlyph glyph, bool isStrong, bool isSuccess, bool hasSeparator, string helpText)
    {
        Text = text;
        Glyph = glyph;
        IsStrong = isStrong;
        IsSuccess = isSuccess;
        HasSeparator = hasSeparator;
        HelpText = helpText;
    }

    public string Text { get; }
    public WinoIconGlyph Glyph { get; }
    public bool IsStrong { get; }
    public bool IsSuccess { get; }
    public bool HasSeparator { get; }
    public string HelpText { get; }

    public bool HasGlyph => Glyph != WinoIconGlyph.None && !IsSuccess;
    public bool HasSuccessGlyph => Glyph != WinoIconGlyph.None && IsSuccess;
    public bool IsPlain => !IsStrong;
    public string? ToolTip => string.IsNullOrEmpty(HelpText) ? null : HelpText;
}
