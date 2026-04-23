using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

public sealed class ReleaseLocalAccountDataCleanupService
{
    private const string CleanupCompletedSettingKey = "ReleaseLocalAccountDataCleanup_v1_Completed";

    private readonly IConfigurationService _configurationService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly ILogger _logger = Log.ForContext<ReleaseLocalAccountDataCleanupService>();

    public ReleaseLocalAccountDataCleanupService(IConfigurationService configurationService,
                                                 IApplicationConfiguration applicationConfiguration)
    {
        _configurationService = configurationService;
        _applicationConfiguration = applicationConfiguration;
    }

    public async Task RunIfNeededAsync()
    {
        if (_configurationService.Get(CleanupCompletedSettingKey, false))
            return;

        var localFolderPath = _applicationConfiguration.ApplicationDataFolderPath;

        if (string.IsNullOrWhiteSpace(localFolderPath) || !Directory.Exists(localFolderPath))
        {
            _configurationService.Set(CleanupCompletedSettingKey, true);
            return;
        }

        var cleanupTargets = new List<string>
        {
            Path.Combine(localFolderPath, "Mime"),
            Path.Combine(localFolderPath, "contacts"),
            Path.Combine(localFolderPath, "CalendarAttachments")
        };

        foreach (var targetPath in cleanupTargets)
        {
            await DeletePathIfExistsAsync(localFolderPath, targetPath).ConfigureAwait(false);
        }

        _configurationService.Set(CleanupCompletedSettingKey, true);
        _logger.Information("Completed one-time local account data cleanup for release migration.");
    }

    private async Task DeletePathIfExistsAsync(string localFolderPath, string targetPath)
    {
        try
        {
            var fullTargetPath = Path.GetFullPath(targetPath);
            var fullLocalFolderPath = Path.GetFullPath(localFolderPath);

            if (!fullTargetPath.StartsWith(fullLocalFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("Skipped startup cleanup for path outside local folder: {TargetPath}", fullTargetPath);
                return;
            }

            if (Directory.Exists(fullTargetPath))
            {
                await Task.Run(() => Directory.Delete(fullTargetPath, recursive: true)).ConfigureAwait(false);
                _logger.Information("Deleted legacy startup cleanup directory {TargetPath}", fullTargetPath);
                return;
            }

            if (File.Exists(fullTargetPath))
            {
                File.Delete(fullTargetPath);
                _logger.Information("Deleted legacy startup cleanup file {TargetPath}", fullTargetPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete legacy startup cleanup path {TargetPath}", targetPath);
        }
    }
}
