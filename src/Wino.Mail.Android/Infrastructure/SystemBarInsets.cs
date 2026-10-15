using Android.Views;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Forwards to <see cref="WinoInsets"/> in controls/Wino.Presentation.Android. New code uses <see cref="WinoInsets"/>
/// directly; this shim keeps the existing pages compiling.
/// </summary>
public static class SystemBarInsets
{
    /// <summary>Pads <paramref name="view"/> by the system bar and cutout insets on every inset change.</summary>
    public static void Apply(View view, bool top = true, bool bottom = true) => WinoInsets.Apply(view, top, bottom);
}
