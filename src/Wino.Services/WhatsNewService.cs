using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.WhatsNew;

namespace Wino.Services;

/// <summary>
/// Reads the per-version release notes bundled under Assets\WhatsNew and tracks the version the
/// What's New window was last opened for.
/// </summary>
public class WhatsNewService : IWhatsNewService
{
    /// <summary>
    /// Stored through IConfigurationService rather than IPreferencesService, so settings
    /// import/export does not carry the seen state to another machine.
    /// </summary>
    internal const string LastOpenedVersionKey = "WhatsNew_LastOpenedVersion";

    private readonly ILogger _logger = Log.ForContext<WhatsNewService>();
    private readonly IConfigurationService _configurationService;
    private readonly INativeAppService _nativeAppService;
    private readonly string _releaseNotesDirectory;

    private IReadOnlyList<WhatsNewRelease>? _releases;

    public WhatsNewService(IConfigurationService configurationService, INativeAppService nativeAppService)
        : this(configurationService, nativeAppService, Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsNew"))
    {
    }

    internal WhatsNewService(IConfigurationService configurationService,
                             INativeAppService nativeAppService,
                             string releaseNotesDirectory)
    {
        _configurationService = configurationService;
        _nativeAppService = nativeAppService;
        _releaseNotesDirectory = releaseNotesDirectory;
    }

    public async Task<IReadOnlyList<WhatsNewRelease>> GetReleasesAsync()
    {
        // The notes are package content and cannot change while the app runs.
        if (_releases != null)
            return _releases;

        var releases = new List<WhatsNewRelease>();

        if (Directory.Exists(_releaseNotesDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_releaseNotesDirectory, "*.json"))
            {
                var release = await ReadReleaseAsync(path).ConfigureAwait(false);
                if (release != null)
                    releases.Add(release);
            }
        }

        _releases = releases
            .OrderByDescending(release => release.ParsedVersion)
            .ToList();

        return _releases;
    }

    public async Task<bool> ShouldShowShellEntryAsync()
    {
        if (!WhatsNewRelease.TryNormalizeVersion(_nativeAppService.GetFullAppVersion(), out var currentVersion))
            return false;

        var releases = await GetReleasesAsync().ConfigureAwait(false);
        if (!releases.Any(release => release.ParsedVersion == currentVersion))
            return false;

        var lastOpened = _configurationService.Get(LastOpenedVersionKey, string.Empty);
        return !WhatsNewRelease.TryNormalizeVersion(lastOpened, out var lastOpenedVersion)
               || lastOpenedVersion < currentVersion;
    }

    public void MarkOpenedForCurrentVersion()
    {
        if (!WhatsNewRelease.TryNormalizeVersion(_nativeAppService.GetFullAppVersion(), out var currentVersion))
            return;

        _configurationService.Set(LastOpenedVersionKey, currentVersion.ToString());
    }

    private async Task<WhatsNewRelease?> ReadReleaseAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, BasicTypesJsonContext.Default.WhatsNewRelease);

            if (release?.ParsedVersion == null)
            {
                _logger.Warning("Skipping What's New file {File}: the version is missing or invalid.", Path.GetFileName(path));
                return null;
            }

            release.Features = release.Features
                .Where(feature => !string.IsNullOrWhiteSpace(feature.Title))
                .ToList();

            return release;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Skipping What's New file {File}: it could not be read.", Path.GetFileName(path));
            return null;
        }
    }
}
