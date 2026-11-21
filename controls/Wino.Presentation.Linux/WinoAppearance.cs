using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux.Style;

namespace Wino.Presentation.Linux;

/// <summary>
/// The process-wide appearance the theme service applied: whether a Wino theme paints the window (zones float, macOS
/// Revision 3), whether the window is dark, the theme accent (null: the system accent) and the icon style. Main thread only.
/// Register with <c>Changed.Add(this, static self =&gt; ...)</c>; a static C# event would root recycled cells.
/// </summary>
public static class WinoAppearance
{
    public const string HasBackdropClass = WinoCssClass.HasBackdrop;

    public static bool HasBackdrop { get; private set; }
    public static bool IsDark { get; private set; }
    public static WinoRgba? Accent { get; private set; }
    public static WinoIconStyle IconStyle { get; private set; }

    public static WinoWeakEvent Changed { get; } = new();
    public static WinoWeakEvent IconStyleChanged { get; } = new();

    /// <summary>Stores the applied appearance and raises <see cref="Changed"/> when any value differs.</summary>
    public static void Update(bool hasBackdrop, bool isDark, WinoRgba? accent)
    {
        if (HasBackdrop == hasBackdrop && IsDark == isDark && Accent == accent) return;
        HasBackdrop = hasBackdrop;
        IsDark = isDark;
        Accent = accent;
        Changed.Raise();
    }

    public static void SetIconStyle(WinoIconStyle style)
    {
        if (IconStyle == style) return;
        IconStyle = style;
        IconStyleChanged.Raise();
    }
}
