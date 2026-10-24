#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Browsers;

namespace Wino.Platform.Windows.Services;

/// <summary>
/// Lists the browsers registered for the Default apps page and their Chromium or Firefox profiles.
/// A browser id is its StartMenuInternet key name; a profile id is the Chromium profile folder or the Firefox profile name.
/// </summary>
public sealed class WindowsInstalledBrowserService : IInstalledBrowserService
{
    private const string StartMenuInternetPath = @"SOFTWARE\Clients\StartMenuInternet";

    public bool IsSupported => true;

    public Task<BrowserCatalog> GetBrowsersAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            try
            {
                var browsers = new List<BrowserOption>();

                foreach (var browser in ReadRegisteredBrowsers())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    browsers.AddRange(GetOptions(browser));
                }

                return new BrowserCatalog(true, browsers);
            }
            catch (OperationCanceledException)
            {
                return BrowserCatalog.Unavailable;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not list the installed browsers.");
                return BrowserCatalog.Unavailable;
            }
        }, CancellationToken.None);

    public Task<bool> TryLaunchAsync(BrowserOption browser, Uri uri, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            try
            {
                var registered = ReadRegisteredBrowsers().FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, browser.BrowserId, StringComparison.OrdinalIgnoreCase));

                if (registered is null || !File.Exists(registered.ExecutablePath)) return false;

                var startInfo = new ProcessStartInfo(registered.ExecutablePath) { UseShellExecute = false };

                if (!string.IsNullOrEmpty(browser.ProfileId))
                {
                    if (registered.IsFirefox)
                    {
                        startInfo.ArgumentList.Add("-P");
                        startInfo.ArgumentList.Add(browser.ProfileId);
                    }
                    else
                    {
                        startInfo.ArgumentList.Add($"--profile-directory={browser.ProfileId}");
                    }
                }

                // AbsoluteUri keeps the escaped form, so spaces stay %20 and cannot split the argument.
                startInfo.ArgumentList.Add(uri.AbsoluteUri);

                using var process = Process.Start(startInfo);
                return process is not null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open a link in the selected browser.");
                return false;
            }
        }, CancellationToken.None);

    private static List<RegisteredBrowser> ReadRegisteredBrowsers()
    {
        var browsers = new Dictionary<string, RegisteredBrowser>(StringComparer.OrdinalIgnoreCase);

        // Per-user registrations come first so they win over machine-wide keys with the same name.
        ReadRegisteredBrowsers(RegistryHive.CurrentUser, RegistryView.Default, browsers);
        ReadRegisteredBrowsers(RegistryHive.LocalMachine, RegistryView.Registry64, browsers);
        ReadRegisteredBrowsers(RegistryHive.LocalMachine, RegistryView.Registry32, browsers);

        return [.. browsers.Values.OrderBy(browser => browser.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static void ReadRegisteredBrowsers(RegistryHive hive, RegistryView view, Dictionary<string, RegisteredBrowser> browsers)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var clients = baseKey.OpenSubKey(StartMenuInternetPath);
            if (clients is null) return;

            foreach (var keyName in clients.GetSubKeyNames())
            {
                if (browsers.ContainsKey(keyName)) continue;

                try
                {
                    using var browserKey = clients.OpenSubKey(keyName);
                    if (browserKey is null) continue;

                    using var commandKey = browserKey.OpenSubKey(@"shell\open\command");
                    var executablePath = ParseExecutablePath(commandKey?.GetValue(null) as string);
                    if (executablePath is null || !File.Exists(executablePath)) continue;

                    // Internet Explorer is still registered on Windows 11, but launching it redirects to Edge.
                    if (string.Equals(Path.GetFileName(executablePath), "iexplore.exe", StringComparison.OrdinalIgnoreCase)) continue;

                    var name = browserKey.GetValue(null) as string;
                    browsers[keyName] = new RegisteredBrowser(keyName, string.IsNullOrWhiteSpace(name) ? keyName : name.Trim(), executablePath);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Skipped an unreadable browser registration.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read browser registrations from {Hive}.", hive);
        }
    }

    private static string? ParseExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        command = Environment.ExpandEnvironmentVariables(command.Trim());

        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : null;
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex > 0 ? command[..(exeIndex + 4)] : null;
    }

    private static IEnumerable<BrowserOption> GetOptions(RegisteredBrowser browser)
    {
        List<BrowserProfile> profiles;

        try
        {
            profiles = browser.IsFirefox ? ReadFirefoxProfiles() : ReadChromiumProfiles(browser.ExecutablePath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the profiles of {Browser}.", browser.Name);
            profiles = [];
        }

        if (profiles.Count == 0)
            return [new BrowserOption(browser.Id, null, browser.Name)];

        return profiles.Select(profile => new BrowserOption(browser.Id, profile.Id, $"{browser.Name} – {profile.DisplayName}"));
    }

    /// <summary>
    /// Chromium browsers install to "...\Vendor\Product\Application\browser.exe" and keep profiles in
    /// "%LOCALAPPDATA%\Vendor\Product\User Data" (Edge, Chrome, Brave), or "%LOCALAPPDATA%\Product\User Data" (Vivaldi).
    /// </summary>
    private static List<BrowserProfile> ReadChromiumProfiles(string executablePath)
    {
        var applicationDirectory = Path.GetDirectoryName(executablePath);
        if (!string.Equals(Path.GetFileName(applicationDirectory), "Application", StringComparison.OrdinalIgnoreCase)) return [];

        var productDirectory = Path.GetDirectoryName(applicationDirectory);
        var product = Path.GetFileName(productDirectory);
        var vendor = Path.GetFileName(Path.GetDirectoryName(productDirectory));
        if (string.IsNullOrEmpty(product)) return [];

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates = string.IsNullOrEmpty(vendor)
            ? [Path.Combine(localAppData, product, "User Data", "Local State")]
            : [Path.Combine(localAppData, vendor, product, "User Data", "Local State"), Path.Combine(localAppData, product, "User Data", "Local State")];

        var localStatePath = candidates.FirstOrDefault(File.Exists);
        if (localStatePath is null) return [];

        using var stream = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("profile", out var profile) ||
            !profile.TryGetProperty("info_cache", out var infoCache) ||
            infoCache.ValueKind != JsonValueKind.Object)
            return [];

        var profiles = new List<BrowserProfile>();

        foreach (var entry in infoCache.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;

            var name = GetString(entry.Value, "name");
            var userName = GetString(entry.Value, "user_name");
            var displayName = string.IsNullOrWhiteSpace(name) ? entry.Name : name;

            if (!string.IsNullOrWhiteSpace(userName) && !string.Equals(userName, displayName, StringComparison.OrdinalIgnoreCase))
                displayName = $"{displayName} ({userName})";

            profiles.Add(new BrowserProfile(entry.Name, displayName));
        }

        return [.. profiles.OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<BrowserProfile> ReadFirefoxProfiles()
    {
        var profilesIniPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "profiles.ini");
        if (!File.Exists(profilesIniPath)) return [];

        var profiles = new List<BrowserProfile>();
        var isProfileSection = false;

        foreach (var rawLine in File.ReadLines(profilesIniPath))
        {
            var line = rawLine.Trim();

            if (line.StartsWith('['))
            {
                isProfileSection = line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (isProfileSection && line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
            {
                var name = line["Name=".Length..].Trim();
                if (name.Length > 0) profiles.Add(new BrowserProfile(name, name));
            }
        }

        return [.. profiles.OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private sealed record RegisteredBrowser(string Id, string Name, string ExecutablePath)
    {
        public bool IsFirefox => string.Equals(Path.GetFileName(ExecutablePath), "firefox.exe", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BrowserProfile(string Id, string DisplayName);
}
