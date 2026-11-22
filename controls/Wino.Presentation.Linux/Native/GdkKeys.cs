using System.Runtime.InteropServices;

namespace Wino.Presentation.Linux.Native;

// Gir.Core 0.8.1 generates gdk_display_translate_key without usable out parameters.
public static partial class GdkKeys
{
    /// <summary>The key a hardware key produces without modifiers in keyboard layout <paramref name="group"/>; 0 when it produces none.</summary>
    public static uint TranslateKey(Gdk.Display display, uint keycode, int group)
    {
        if (keycode == 0) return 0;
        return gdk_display_translate_key(display.Handle.DangerousGetHandle(), keycode, 0, group, out var keyval, out _, out _, out _)
            ? keyval
            : 0;
    }

    [LibraryImport("libgtk-4.so.1")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gdk_display_translate_key(IntPtr display, uint keycode, uint state, int group,
        out uint keyval, out int effectiveGroup, out int level, out uint consumed);
}
