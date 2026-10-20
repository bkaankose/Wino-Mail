using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Wino.Platform.Windows;

/// <summary>Uses native Shell COM interfaces without reflection-based COM interop.</summary>
public static unsafe class WindowsStartMenuShortcut
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterfaceId = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileInterfaceId = new("0000010B-0000-0000-C000-000000000046");

    public static void Create(string file, string target, string arguments, string description, string icon)
    {
        using var link = new LinkHandle();
        link.SetString(20, target);
        link.SetString(11, arguments);
        link.SetString(7, description);
        link.SetIcon(icon);
        link.Persist(file, save: true);
    }

    public static bool MatchesTarget(string file, string target, string arguments)
    {
        if (!File.Exists(file)) return false;
        using var link = new LinkHandle();
        link.Persist(file, save: false);
        return string.Equals(link.GetPath(), target, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.GetArguments(), arguments, StringComparison.Ordinal);
    }

    private sealed class LinkHandle : IDisposable
    {
        private nint _instance;
        private readonly bool _ownsComInitialization;

        public LinkHandle()
        {
            var initialized = CoInitializeEx(0, 0);
            if (initialized < 0 && initialized != unchecked((int)0x80010106))
                Marshal.ThrowExceptionForHR(initialized);
            _ownsComInitialization = initialized >= 0;
            var classId = ShellLinkClassId;
            var interfaceId = ShellLinkInterfaceId;
            try
            {
                Marshal.ThrowExceptionForHR(CoCreateInstance(&classId, 0, 1, &interfaceId, out _instance));
            }
            catch
            {
                if (_ownsComInitialization) CoUninitialize();
                throw;
            }
        }

        public void SetString(int slot, string value)
        {
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)_instance)[slot];
            fixed (char* text = value)
                Marshal.ThrowExceptionForHR(method(_instance, text));
        }

        public void SetIcon(string path)
        {
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)_instance)[17];
            fixed (char* text = path)
                Marshal.ThrowExceptionForHR(method(_instance, text, 0));
        }

        public string GetPath()
        {
            var text = stackalloc char[1024];
            new Span<char>(text, 1024).Clear();
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)(*(nint**)_instance)[3];
            Marshal.ThrowExceptionForHR(method(_instance, text, 1024, 0, 4));
            return new string(text);
        }

        public string GetArguments()
        {
            var text = stackalloc char[1024];
            new Span<char>(text, 1024).Clear();
            var method = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)_instance)[10];
            Marshal.ThrowExceptionForHR(method(_instance, text, 1024));
            return new string(text);
        }

        public void Persist(string file, bool save)
        {
            nint persist = 0;
            var interfaceId = PersistFileInterfaceId;
            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)_instance)[0];
            Marshal.ThrowExceptionForHR(query(_instance, &interfaceId, &persist));
            try
            {
                fixed (char* path = file)
                {
                    if (save)
                    {
                        var method = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)persist)[6];
                        Marshal.ThrowExceptionForHR(method(persist, path, 1));
                    }
                    else
                    {
                        var method = (delegate* unmanaged[Stdcall]<nint, char*, uint, int>)(*(nint**)persist)[5];
                        Marshal.ThrowExceptionForHR(method(persist, path, 0));
                    }
                }
            }
            finally { Release(persist); }
        }

        public void Dispose()
        {
            if (_instance == 0) return;
            Release(_instance);
            _instance = 0;
            if (_ownsComInitialization) CoUninitialize();
        }

        private static void Release(nint instance)
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2];
            _ = release(instance);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* interfaceId, out nint instance);
}
