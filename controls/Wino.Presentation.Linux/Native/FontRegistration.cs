using System.Runtime.InteropServices;

namespace Wino.Presentation.Linux.Native;

// The fallback for Pango older than 1.56, which has no Pango.FontMap.AddFontFile. It must run before the first Pango
// font map exists: fontconfig fonts added later are not seen by a map that is already built.
internal static partial class FontRegistration
{
    public static bool AddApplicationFont(string path) => FcConfigAppFontAddFile(IntPtr.Zero, path) == 1;

    [LibraryImport("libfontconfig.so.1", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int FcConfigAppFontAddFile(IntPtr config, string file);
}
