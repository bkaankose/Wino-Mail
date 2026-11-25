using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Wino.Platform.Linux.Native;

/// <summary>
/// Freedesktop sound-theme events through libcanberra, the library GNOME plays its own event sounds with. Only its
/// non-variadic functions are called (<c>ca_context_play</c> and <c>ca_context_change_props</c> are variadic). One
/// context lives for the process; plays are serialised because a context is not meant for concurrent use.
/// </summary>
internal static unsafe partial class Canberra
{
    private const string Library = "libcanberra.so.0";
    private const int Success = 0;
    private static readonly Lock Gate = new();
    private static nint _context;
    private static uint _nextId;
    private static bool _unavailable;
    private static bool _reported;

    /// <summary>True once the library or the sound server failed to load; plays are skipped from then on.</summary>
    public static bool IsUnavailable
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("WINO_NO_CANBERRA") == "1") return true;
#endif
            return _unavailable;
        }
    }

    /// <summary>The last error text, or null.</summary>
    public static string? LastError { get; private set; }

    /// <summary>The finish code of the last play that ended (0 = played to the end), or null while none ended.</summary>
    public static int? LastFinished { get; private set; }

    public static uint LastFinishedId { get; private set; }

    public static uint LastId => _nextId;

    public static int Started { get; private set; }

    /// <summary>Starts playing the theme event; returns at once. False when the library or the sound server refused it.</summary>
    public static bool TryPlay(string eventId, string description)
    {
        if (IsUnavailable) return false;
        lock (Gate)
        {
            try
            {
                if (_context == 0 && !Open()) return false;
                var properties = nint.Zero;
                var result = ca_proplist_create(out properties);
                if (result != Success) return Fail(result, "proplist");
                try
                {
                    ca_proplist_sets(properties, "event.id", eventId);
                    ca_proplist_sets(properties, "event.description", description);
                    ca_proplist_sets(properties, "media.role", "event");
                    ca_proplist_sets(properties, "canberra.cache-control", "volatile");
                    result = ca_context_play_full(_context, ++_nextId, properties, &Finished, nint.Zero);
                }
                finally
                {
                    ca_proplist_destroy(properties);
                }
                if (result != Success) return Fail(result, eventId);
                Started++;
                return true;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                _unavailable = true;
                LastError = exception.Message;
                Serilog.Log.Warning("Notification sounds are off: libcanberra is not available ({Error}).", exception.Message);
                return false;
            }
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            if (_context == 0) return;
            try { ca_context_destroy(_context); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
            _context = 0;
        }
    }

    private static bool Open()
    {
        var result = ca_context_create(out var context);
        if (result != Success) return Fail(result, "create");
        var properties = nint.Zero;
        if (ca_proplist_create(out properties) == Success)
        {
            ca_proplist_sets(properties, "application.name", "Wino Mail");
            ca_proplist_sets(properties, "application.id", "app.winomail.Mail");
            ca_proplist_sets(properties, "application.icon_name", "app.winomail.Mail");
            ca_context_change_props_full(context, properties);
            ca_proplist_destroy(properties);
        }
        result = ca_context_open(context);
        if (result != Success)
        {
            ca_context_destroy(context);
            // Without a sound server every later play fails the same way.
            _unavailable = true;
            return Fail(result, "open");
        }
        _context = context;
        return true;
    }

    private static bool Fail(int code, string step)
    {
        LastError = $"{step}: {Marshal.PtrToStringUTF8(ca_strerror(code)) ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (!_reported || _unavailable)
        {
            _reported = true;
            Serilog.Log.Warning("A notification sound could not play: {Error}.", LastError);
        }
        return false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Finished(nint context, uint id, int code, nint userData)
    {
        LastFinished = code;
        LastFinishedId = id;
    }

    [LibraryImport(Library)]
    private static partial int ca_context_create(out nint context);

    [LibraryImport(Library)]
    private static partial int ca_context_open(nint context);

    [LibraryImport(Library)]
    private static partial int ca_context_destroy(nint context);

    [LibraryImport(Library)]
    private static partial int ca_context_change_props_full(nint context, nint properties);

    [LibraryImport(Library)]
    private static partial int ca_context_play_full(nint context, uint id, nint properties, delegate* unmanaged[Cdecl]<nint, uint, int, nint, void> finished, nint userData);

    [LibraryImport(Library)]
    private static partial int ca_proplist_create(out nint properties);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ca_proplist_sets(nint properties, string key, string value);

    [LibraryImport(Library)]
    private static partial int ca_proplist_destroy(nint properties);

    [LibraryImport(Library)]
    private static partial nint ca_strerror(int code);
}

/// <summary>libcanberra's state for diagnostics: whether it loaded, how many plays started and how the last one ended.</summary>
public static class CanberraState
{
    public static bool IsUnavailable => Canberra.IsUnavailable;

    public static int Started => Canberra.Started;

    public static uint LastId => Canberra.LastId;

    /// <summary>The finish code of the last play that ended, when one with a later id than <paramref name="id"/> ended.</summary>
    public static int? FinishedAfter(uint id) => Canberra.LastFinishedId > id ? Canberra.LastFinished : null;

    public static int? LastFinished => Canberra.LastFinished;

    public static string? LastError => Canberra.LastError;
}
