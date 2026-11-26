#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wino.Core.Services.MailClientImport;

/// <summary>
/// Where each client keeps its account files on this operating system. A path may not exist; readers skip it. Sandboxed
/// apps (the Mac App Store and iOS) see their own container as the home folder, so these paths find nothing there.
/// </summary>
internal static class MailClientImportLocations
{
    public static IEnumerable<string> GetThunderbirdRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(ApplicationData, "Thunderbird");

            // The Microsoft Store build keeps its profile inside its package folder.
            foreach (var package in EnumerateDirectories(Path.Combine(LocalApplicationData, "Packages"), "Mozilla.Thunderbird*"))
                yield return Path.Combine(package, "LocalCache", "Roaming", "Thunderbird");

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(Home, "Library", "Thunderbird");
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(Home, ".thunderbird");
            yield return Path.Combine(XdgConfigHome, "thunderbird");
            yield return Path.Combine(Home, "snap", "thunderbird", "common", ".thunderbird");
            yield return Path.Combine(Home, ".var", "app", "org.mozilla.Thunderbird", ".thunderbird");
        }
    }

    public static string GetNewOutlookSettingsPath()
        => Path.Combine(LocalApplicationData, "Microsoft", "Olk", "UserSettings.json");

    public static IEnumerable<string> GetEvolutionSourceFolders()
    {
        yield return Path.Combine(XdgConfigHome, "evolution", "sources");
        yield return Path.Combine(Home, ".var", "app", "org.gnome.Evolution", "config", "evolution", "sources");
    }

    public static string GetGnomeOnlineAccountsPath()
        => Path.Combine(XdgConfigHome, "goa-1.0", "accounts.conf");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string ApplicationData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalApplicationData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string XdgConfigHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return string.IsNullOrWhiteSpace(configured) || !Path.IsPathRooted(configured)
                ? Path.Combine(Home, ".config")
                : configured;
        }
    }

    private static List<string> EnumerateDirectories(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, pattern).ToList() : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
