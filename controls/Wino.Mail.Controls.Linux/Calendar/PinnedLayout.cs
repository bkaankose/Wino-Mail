using System.Runtime.InteropServices;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>A cached Pango layout with its pixel size, holding one extra native reference while it is cached.</summary>
internal sealed partial class PinnedLayout : IDisposable
{
    private IntPtr _handle;

    public PinnedLayout(Pango.Layout layout)
    {
        Layout = layout;
        // The extra reference keeps Gir.Core's toggle reference strong, so pango_cairo_show_layout's ref/unref pair
        // never calls back into managed code (SP-04: about 25 B per draw call otherwise).
        _handle = layout.Handle.DangerousGetHandle();
        g_object_ref(_handle);
        layout.GetPixelSize(out var width, out var height);
        Width = width;
        Height = height;
    }

    public Pango.Layout Layout { get; }

    public int Width { get; }

    public int Height { get; }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) g_object_unref(handle);
    }

    [LibraryImport("libgobject-2.0.so.0")]
    private static partial IntPtr g_object_ref(IntPtr instance);

    [LibraryImport("libgobject-2.0.so.0")]
    private static partial void g_object_unref(IntPtr instance);
}
