namespace Wino.Presentation.Linux;

public static class WinoPresentation
{
    private static bool _initialized;

    /// <summary>Registers the GObject types this assembly defines. Call once, on the main thread, before the application object is created.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Gtk.Module.Initialize();
        Adw.Module.Initialize();
        global::GirCore.Integration.Initialize();
    }
}
