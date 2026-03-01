using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Services;

public class UpdateManager : IUpdateManager
{
    private const string UpdateNotesResourcePath = "ms-appx:///Assets/UpdateNotes/vnext.json";
    private const string UpdateNotesSeenKeyFormat = "UpdateNotes_{0}_Shown";
    private const string MigrationCompletedKeyFormat = "Migration_{0}_Completed";

    private readonly IFileService _fileService;
    private readonly IConfigurationService _configurationService;
    private readonly INativeAppService _nativeAppService;
    private readonly List<IAppMigration> _migrations = [];

    private string _versionSeenKey = string.Empty;

    public UpdateManager(IFileService fileService,
                         IConfigurationService configurationService,
                         INativeAppService nativeAppService)
    {
        _fileService = fileService;
        _configurationService = configurationService;
        _nativeAppService = nativeAppService;
    }

    private string GetVersionSeenKey()
    {
        if (string.IsNullOrEmpty(_versionSeenKey))
        {
            var version = _nativeAppService.GetFullAppVersion();
            var sanitized = version.Replace(".", "_");
            _versionSeenKey = string.Format(UpdateNotesSeenKeyFormat, sanitized);
        }

        return _versionSeenKey;
    }

    public async Task<UpdateNotes> GetLatestUpdateNotesAsync()
    {
        try
        {
            var json = await _fileService.GetFileContentByApplicationUriAsync(UpdateNotesResourcePath);

            if (string.IsNullOrEmpty(json))
                return new UpdateNotes();

            return JsonSerializer.Deserialize(json, BasicTypesJsonContext.Default.UpdateNotes) ?? new UpdateNotes();
        }
        catch (Exception)
        {
            return new UpdateNotes();
        }
    }

    public bool ShouldShowUpdateNotes()
        => !_configurationService.Get(GetVersionSeenKey(), false);

    public void MarkUpdateNotesAsSeen()
        => _configurationService.Set(GetVersionSeenKey(), true);

    public bool HasPendingMigrations()
        => _migrations.Any(m => !_configurationService.Get(string.Format(MigrationCompletedKeyFormat, m.MigrationId), false));

    public async Task RunPendingMigrationsAsync()
    {
        foreach (var migration in _migrations)
        {
            var key = string.Format(MigrationCompletedKeyFormat, migration.MigrationId);

            if (!_configurationService.Get(key, false))
            {
                await migration.ExecuteAsync();
                _configurationService.Set(key, true);
            }
        }
    }

    public void RegisterMigrations(IEnumerable<IAppMigration> migrations)
        => _migrations.AddRange(migrations);
}
