using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Windows;

namespace Wino.Mail.WinUI.Services;

/// <summary>Manages only the three optional shortcuts belonging to this package.</summary>
public sealed class WindowsStartMenuIntegrationService(IPreferencesService preferences, IWinoLogger logger)
    : IStartMenuIntegrationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly WinoApplicationMode[] OptionalModes =
        [WinoApplicationMode.Calendar, WinoApplicationMode.Contacts, WinoApplicationMode.Tasks];

    public bool IsEnabled(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => preferences.IsCalendarStartMenuEntryEnabled,
        WinoApplicationMode.Contacts => preferences.IsContactsStartMenuEntryEnabled,
        WinoApplicationMode.Tasks => preferences.IsTasksStartMenuEntryEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public async Task SetEnabledAsync(WinoApplicationMode mode, bool enabled)
    {
        // Validate the mode before touching the shell. Mail never has a removable shortcut.
        _ = IsEnabled(mode);
        await _gate.WaitAsync();
        try
        {
            var entry = GetEntry(mode);
            var previous = await Task.Run(() =>
            {
                ValidateOwnership(entry.Path, entry.Arguments);
                return File.Exists(entry.Path) ? File.ReadAllBytes(entry.Path) : null;
            });
            try
            {
                await Task.Run(() => Apply(mode, enabled));
                switch (mode)
                {
                    case WinoApplicationMode.Calendar: preferences.IsCalendarStartMenuEntryEnabled = enabled; break;
                    case WinoApplicationMode.Contacts: preferences.IsContactsStartMenuEntryEnabled = enabled; break;
                    case WinoApplicationMode.Tasks: preferences.IsTasksStartMenuEntryEnabled = enabled; break;
                }
            }
            catch
            {
                // Restore the previous shell state if either the update or preference save fails.
                try
                {
                    await Task.Run(() =>
                    {
                        ValidateOwnership(entry.Path, entry.Arguments);
                        if (previous is null) File.Delete(entry.Path);
                        else WriteAtomic(entry.Path, previous);
                        NotifyShell(entry.Path, previous is not null);
                    });
                }
                catch (Exception rollbackFailure)
                {
                    logger.CaptureException(rollbackFailure, "RestoreStartMenuEntry");
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SynchronizeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var mode in OptionalModes)
            {
                try { await Task.Run(() => Apply(mode, IsEnabled(mode))).ConfigureAwait(false); }
                catch (Exception exception) { logger.CaptureException(exception, "SynchronizeStartMenuEntry"); }
            }
        }
        finally { _gate.Release(); }
    }

    private static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    private static (string Path, string Arguments, string AssetFolder, string Title) GetEntry(WinoApplicationMode mode)
    {
        var (appId, title, assets) = mode switch
        {
            WinoApplicationMode.Calendar => ("CalendarApp", "Wino Calendar", "CalendarAssets"),
            WinoApplicationMode.Contacts => ("ContactsApp", "Wino People", "PeopleAssets"),
            WinoApplicationMode.Tasks => ("ToDoApp", "Wino To Do", "ToDoAssets"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programs)) throw new DirectoryNotFoundException("The Start menu folder is unavailable.");
        return (Path.Combine(programs, "Wino Mail", title + ".lnk"),
            $"shell:AppsFolder\\{Package.Current.Id.FamilyName}!{appId}", assets, title);
    }

    private static void ValidateOwnership(string path, string arguments)
    {
        if (File.Exists(path) && !WindowsStartMenuShortcut.MatchesTarget(path, ExplorerPath, arguments))
            throw new IOException("An existing Start menu shortcut belongs to another application or installation.");
    }

    private static void Apply(WinoApplicationMode mode, bool enabled)
    {
        var entry = GetEntry(mode);
        ValidateOwnership(entry.Path, entry.Arguments);
        if (!enabled)
        {
            if (File.Exists(entry.Path))
            {
                File.Delete(entry.Path);
                NotifyShell(entry.Path, false);
            }
            return;
        }

        var iconDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "StartMenuIcons");
        Directory.CreateDirectory(iconDirectory);
        var iconPath = Path.Combine(iconDirectory, entry.AssetFolder + ".ico");
        var pngPath = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "AppEntries",
            entry.AssetFolder, "Square44x44Logo.targetsize-256.png");
        var png = File.ReadAllBytes(pngPath);
        using var icon = new MemoryStream();
        using (var writer = new BinaryWriter(icon, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(png.Length); writer.Write(22); writer.Write(png);
        }
        var iconBytes = icon.ToArray();
        if (!File.Exists(iconPath) || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(iconBytes))
            WriteAtomic(iconPath, iconBytes);

        // Stable AUMIDs and icon paths survive package updates. Do not recreate existing links
        // at every launch, which would make Windows repeatedly treat them as newly installed.
        if (File.Exists(entry.Path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(entry.Path) ?? throw new IOException("Invalid shortcut path."));
        var temporary = entry.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WindowsStartMenuShortcut.Create(temporary, ExplorerPath, entry.Arguments, entry.Title, iconPath);
            File.Move(temporary, entry.Path);
        }
        finally { File.Delete(temporary); }
        NotifyShell(entry.Path, true);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { File.Delete(temporary); }
    }

    private static void NotifyShell(string path, bool exists) => SHChangeNotify(exists ? 2U : 4U, 0x2005, path, 0);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint change, uint flags, string item, nint unused);
}
