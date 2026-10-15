using Android.Content;
using Android.Graphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Forwards to <see cref="WinoIcons"/> in controls/Wino.Presentation.Android, which owns the WinoIcons fonts. New code
/// uses <see cref="WinoIcons"/> directly; this shim keeps the existing onboarding pages compiling.
/// </summary>
public static class WinoIconFonts
{
    public static Typeface Mono(Context context) => WinoIcons.Mono(context);

    /// <summary>The colorful font for the appearance of <paramref name="context"/>.</summary>
    public static Typeface Colorful(Context context) => WinoIcons.Colorful(context);

    public static string Glyph(WinoIconGlyph icon) => WinoIcons.Glyph(icon);

    public static bool IsDark(Context context) => WinoColors.IsDark(context);

    /// <summary>"glyph  text" with the glyph drawn in <paramref name="typeface"/>, for buttons that carry a Wino icon.</summary>
    public static Java.Lang.ICharSequence WithGlyph(WinoIconGlyph icon, string text, Typeface typeface) => WinoIcons.WithGlyph(icon, text, typeface);
}
