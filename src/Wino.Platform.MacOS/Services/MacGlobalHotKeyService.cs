using System.Runtime.InteropServices;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// One system-wide hotkey through Carbon RegisterEventHotKey (the macOS counterpart of the Windows
/// NativeTrayIcon.TrySetHotKey). Carbon hotkeys work in the sandbox and need no Accessibility
/// permission. Like Windows, a new combination is registered under the other of two ids before the
/// previous one is removed, so a failed change keeps the previous shortcut active.
/// Stored gestures use the Windows VirtualKey names (see <see cref="MacHotKeyKeys"/>); Control,
/// Alt, Shift and Command (or Windows) map to controlKey, optionKey, shiftKey and cmdKey.
/// Must be used on the main thread; <see cref="Pressed"/> is raised there.
/// </summary>
public sealed class MacGlobalHotKeyService : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint Signature = 0x57494E4F;          // 'WINO'
    private const uint PrimaryId = 1;
    private const uint SecondaryId = 2;
    private const uint KeyboardEventClass = 0x6B657962; // kEventClassKeyboard 'keyb'
    private const uint HotKeyPressedKind = 5;           // kEventHotKeyPressed
    private const uint DirectObjectParameter = 0x2D2D2D2D; // kEventParamDirectObject '----'
    private const uint HotKeyIdType = 0x686B6964;       // typeEventHotKeyID 'hkid'
    private const int HotKeyExistsError = -9878;        // eventHotKeyExistsErr

    private const uint CmdKey = 0x0100;
    private const uint ShiftKey = 0x0200;
    private const uint OptionKey = 0x0800;
    private const uint ControlKey = 0x1000;

    // The handler must outlive every registration: Carbon keeps the raw function pointer.
    private readonly EventHandlerProc _handler;
    private nint _handlerRef;
    private nint _activeRef;
    private uint _activeId;
    private bool _disposed;

    public MacGlobalHotKeyService()
    {
        _handler = HandleEvent;
    }

    /// <summary>Raised on the main thread when the registered combination is pressed anywhere.</summary>
    public event EventHandler? Pressed;

    /// <summary>The combination registered with the system, or null.</summary>
    public HotKeyGesture? ActiveHotKey { get; private set; }

    /// <summary>Why the last <see cref="TrySetHotKey"/> failed, for diagnostics.</summary>
    public string? LastError { get; private set; }

    /// <summary>Registers <paramref name="gesture"/>, or removes the hotkey when it is null. False keeps the previous one.</summary>
    public bool TrySetHotKey(HotKeyGesture? gesture)
    {
        if (_disposed) return false;
        var normalized = gesture?.Normalize();
        if (normalized == ActiveHotKey)
        {
            LastError = null;
            return true;
        }

        if (normalized is null)
        {
            UnregisterActive();
            LastError = null;
            return true;
        }

        if (!normalized.Value.IsValid || !MacHotKeyKeys.TryGetKeyCode(normalized.Value.Key, out var keyCode))
        {
            LastError = $"invalid gesture {normalized.Value.Modifiers}+{normalized.Value.Key}";
            return false;
        }

        try
        {
            if (!EnsureHandler()) return false;
            var candidateId = _activeId == PrimaryId ? SecondaryId : PrimaryId;
            var status = RegisterEventHotKey(keyCode, ToCarbonModifiers(normalized.Value.Modifiers),
                new EventHotKeyID { Signature = Signature, Id = candidateId }, GetApplicationEventTarget(), 0, out var hotKeyRef);
            if (status != 0)
            {
                LastError = status == HotKeyExistsError ? "the combination is already registered (eventHotKeyExistsErr)" : $"RegisterEventHotKey failed ({status})";
                return false;
            }

            if (_activeRef != 0) UnregisterEventHotKey(_activeRef);
            _activeRef = hotKeyRef;
            _activeId = candidateId;
            ActiveHotKey = normalized;
            LastError = null;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>Runs the pressed handler as if the hotkey fired (debug bridge).</summary>
    public void SimulatePress() => Pressed?.Invoke(this, EventArgs.Empty);

    private bool EnsureHandler()
    {
        if (_handlerRef != 0) return true;
        var spec = new[] { new EventTypeSpec { EventClass = KeyboardEventClass, EventKind = HotKeyPressedKind } };
        var status = InstallEventHandler(GetApplicationEventTarget(), Marshal.GetFunctionPointerForDelegate(_handler), 1, spec, 0, out _handlerRef);
        if (status == 0) return true;
        _handlerRef = 0;
        LastError = $"InstallEventHandler failed ({status})";
        return false;
    }

    private int HandleEvent(nint nextHandler, nint theEvent, nint userData)
    {
        try
        {
            var status = GetEventParameter(theEvent, DirectObjectParameter, HotKeyIdType, 0, (nuint)Marshal.SizeOf<EventHotKeyID>(), 0, out var id);
            if (status == 0 && id.Signature == Signature && id.Id == _activeId && ActiveHotKey is not null)
                Pressed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // An exception must never unwind into Carbon.
        }
        return 0;
    }

    private void UnregisterActive()
    {
        if (_activeRef != 0) UnregisterEventHotKey(_activeRef);
        _activeRef = 0;
        _activeId = 0;
        ActiveHotKey = null;
    }

    private static uint ToCarbonModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= ControlKey;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= OptionKey;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= ShiftKey;
        if (modifiers.HasFlag(ModifierKeys.Command) || modifiers.HasFlag(ModifierKeys.Windows)) result |= CmdKey;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            UnregisterActive();
            if (_handlerRef != 0) RemoveEventHandler(_handlerRef);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        _handlerRef = 0;
        Pressed = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerProc(nint nextHandler, nint theEvent, nint userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [DllImport(Carbon)]
    private static extern nint GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(nint target, nint handler, nuint numTypes, [In] EventTypeSpec[] list, nint userData, out nint handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(nint handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, nint target, uint options, out nint hotKeyRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(nint hotKeyRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(nint theEvent, uint name, uint desiredType, nint actualType, nuint bufferSize, nint actualSize, out EventHotKeyID data);
}

/// <summary>
/// Translates between the Windows VirtualKey names stored in preferences ("Space", "A",
/// "Number1", "F5", "186" for OEM keys) and macOS virtual key codes (kVK_*), which are also what
/// <c>NSEvent.KeyCode</c> reports, and gives the Mac display form of each key.
/// </summary>
public static class MacHotKeyKeys
{
    private static readonly (string Name, ushort Code, string Display)[] Keys =
    [
        ("A", 0x00, "A"), ("S", 0x01, "S"), ("D", 0x02, "D"), ("F", 0x03, "F"), ("H", 0x04, "H"), ("G", 0x05, "G"),
        ("Z", 0x06, "Z"), ("X", 0x07, "X"), ("C", 0x08, "C"), ("V", 0x09, "V"), ("B", 0x0B, "B"), ("Q", 0x0C, "Q"),
        ("W", 0x0D, "W"), ("E", 0x0E, "E"), ("R", 0x0F, "R"), ("Y", 0x10, "Y"), ("T", 0x11, "T"), ("O", 0x1F, "O"),
        ("U", 0x20, "U"), ("I", 0x22, "I"), ("P", 0x23, "P"), ("L", 0x25, "L"), ("J", 0x26, "J"), ("K", 0x28, "K"),
        ("N", 0x2D, "N"), ("M", 0x2E, "M"),
        ("Number1", 0x12, "1"), ("Number2", 0x13, "2"), ("Number3", 0x14, "3"), ("Number4", 0x15, "4"), ("Number6", 0x16, "6"),
        ("Number5", 0x17, "5"), ("Number9", 0x19, "9"), ("Number7", 0x1A, "7"), ("Number8", 0x1C, "8"), ("Number0", 0x1D, "0"),
        ("Space", 0x31, "Space"), ("Enter", 0x24, "↩"), ("Tab", 0x30, "⇥"), ("Back", 0x33, "⌫"), ("Escape", 0x35, "⎋"),
        ("Delete", 0x75, "⌦"), ("Home", 0x73, "↖"), ("End", 0x77, "↘"), ("PageUp", 0x74, "⇞"), ("PageDown", 0x79, "⇟"),
        ("Left", 0x7B, "←"), ("Right", 0x7C, "→"), ("Down", 0x7D, "↓"), ("Up", 0x7E, "↑"),
        ("F1", 0x7A, "F1"), ("F2", 0x78, "F2"), ("F3", 0x63, "F3"), ("F4", 0x76, "F4"), ("F5", 0x60, "F5"), ("F6", 0x61, "F6"),
        ("F7", 0x62, "F7"), ("F8", 0x64, "F8"), ("F9", 0x65, "F9"), ("F10", 0x6D, "F10"), ("F11", 0x67, "F11"), ("F12", 0x6F, "F12"),
        ("F13", 0x69, "F13"), ("F14", 0x6B, "F14"), ("F15", 0x71, "F15"), ("F16", 0x6A, "F16"), ("F17", 0x40, "F17"),
        ("F18", 0x4F, "F18"), ("F19", 0x50, "F19"), ("F20", 0x5A, "F20"),
        // Windows has no VirtualKey names for the OEM keys, so their numeric values are stored.
        ("186", 0x29, ";"), ("187", 0x18, "="), ("188", 0x2B, ","), ("189", 0x1B, "-"), ("190", 0x2F, "."), ("191", 0x2C, "/"),
        ("192", 0x32, "`"), ("219", 0x21, "["), ("220", 0x2A, "\\"), ("221", 0x1E, "]"), ("222", 0x27, "'")
    ];

    public static bool TryGetKeyCode(string? name, out ushort keyCode)
    {
        foreach (var key in Keys)
        {
            if (!string.Equals(key.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            keyCode = key.Code;
            return true;
        }
        keyCode = 0;
        return false;
    }

    public static string? NameForKeyCode(ushort keyCode)
    {
        foreach (var key in Keys)
            if (key.Code == keyCode) return key.Name;
        return null;
    }

    /// <summary>The key as macOS menus show it (⏎ for Return, 1 for Number1).</summary>
    public static string Display(string? name)
    {
        foreach (var key in Keys)
            if (string.Equals(key.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return key.Display;
        return name ?? string.Empty;
    }

    /// <summary>Modifier symbols in macOS order: ⌃ ⌥ ⇧ ⌘.</summary>
    public static IReadOnlyList<string> ModifierSymbols(ModifierKeys modifiers)
    {
        var symbols = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) symbols.Add("⌃");
        if (modifiers.HasFlag(ModifierKeys.Alt)) symbols.Add("⌥");
        if (modifiers.HasFlag(ModifierKeys.Shift)) symbols.Add("⇧");
        if (modifiers.HasFlag(ModifierKeys.Command) || modifiers.HasFlag(ModifierKeys.Windows)) symbols.Add("⌘");
        return symbols;
    }

    /// <summary>The whole gesture as one string, for example ⌃⇧Space.</summary>
    public static string Format(HotKeyGesture gesture) => string.Concat(ModifierSymbols(gesture.Modifiers)) + Display(gesture.Key);
}
