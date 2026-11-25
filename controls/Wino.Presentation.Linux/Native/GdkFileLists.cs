using System.Runtime.InteropServices;

namespace Wino.Presentation.Linux.Native;

// Gir.Core 0.8.1 generates no gdk_file_list_get_files, neither managed nor in its Internal layer.
public static partial class GdkFileLists
{
    /// <summary>Local paths of the GdkFileList boxed in a drop value; files without a local path are skipped.</summary>
    public static string[] GetPaths(GObject.Value value)
    {
        var list = value.GetBoxed();
        if (list == IntPtr.Zero) return [];
        var paths = new List<string>();
        var head = gdk_file_list_get_files(list);
        try
        {
            for (var node = head; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var file = Marshal.ReadIntPtr(node);
                if (file == IntPtr.Zero) continue;
                if (Gio.FileHelper.NewFromPointer(file, false).GetPath() is { Length: > 0 } path) paths.Add(path);
            }
        }
        finally
        {
            // Shallow: the GFiles belong to the file list.
            if (head != IntPtr.Zero) g_slist_free(head);
        }
        return [.. paths];
    }

    [LibraryImport("libgtk-4.so.1")]
    private static partial IntPtr gdk_file_list_get_files(IntPtr list);

    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_slist_free(IntPtr list);
}
