using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

public sealed class ReleaseLocalAccountDataCleanupService
{
    private const string CleanupCompletedSettingKey = "ReleaseLocalAccountDataCleanup_v1_Completed";
    private const string LegacyDatabaseFileName = "Wino180.db";

    private readonly IConfigurationService _configurationService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly ILogger _logger = Log.ForContext<ReleaseLocalAccountDataCleanupService>();

    public ReleaseLocalAccountDataCleanupService(IConfigurationService configurationService,
                                                 IApplicationConfiguration applicationConfiguration,
                                                 INotificationBuilder notificationBuilder)
    {
        _configurationService = configurationService;
        _applicationConfiguration = applicationConfiguration;
        _notificationBuilder = notificationBuilder;
    }

    public async Task RunIfNeededAsync()
    {
        if (_configurationService.Get(CleanupCompletedSettingKey, false))
            return;

        var localFolderPath = _applicationConfiguration.ApplicationDataFolderPath;
        var publisherPath = _applicationConfiguration.PublisherSharedFolderPath;

        if (string.IsNullOrWhiteSpace(localFolderPath) || !Directory.Exists(localFolderPath))
        {
            _configurationService.Set(CleanupCompletedSettingKey, true);
            return;
        }

        var cleanupTargets = new List<string>
        {
            Path.Combine(localFolderPath, "Mime"),
            Path.Combine(localFolderPath, "contacts"),
            Path.Combine(localFolderPath, "CalendarAttachments"),
            Path.Combine(publisherPath, LegacyDatabaseFileName)
        };

        var hadLegacyData = false;

        foreach (var targetPath in cleanupTargets)
        {
            hadLegacyData |= await DeletePathIfExistsAsync(targetPath, localFolderPath, publisherPath).ConfigureAwait(false);
        }

        _configurationService.Set(CleanupCompletedSettingKey, true);

        if (hadLegacyData)
        {
            _notificationBuilder.CreateReleaseMigrationNotification();
        }

        _logger.Information("Completed one-time local account data cleanup for release migration.");
    }

    private async Task<bool> DeletePathIfExistsAsync(string targetPath, params string[] allowedRootPaths)
    {
        try
        {
            var fullTargetPath = Path.GetFullPath(targetPath);
            if (!allowedRootPaths.Any(rootPath => IsPathUnderAllowedRoot(fullTargetPath, rootPath)))
            {
                _logger.Warning("Skipped startup cleanup for path outside allowed roots: {TargetPath}", fullTargetPath);
                return false;
            }

            var targetExists = Directory.Exists(fullTargetPath) || File.Exists(fullTargetPath);

            if (Directory.Exists(fullTargetPath))
            {
                await Task.Run(() => Directory.Delete(fullTargetPath, recursive: true)).ConfigureAwait(false);
                _logger.Information("Deleted legacy startup cleanup directory {TargetPath}", fullTargetPath);
                return true;
            }

            if (File.Exists(fullTargetPath))
            {
                File.Delete(fullTargetPath);
                _logger.Information("Deleted legacy startup cleanup file {TargetPath}", fullTargetPath);
                return true;
            }

            return targetExists;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete legacy startup cleanup path {TargetPath}", targetPath);
        }

        return false;
    }

    private static bool IsPathUnderAllowedRoot(string fullTargetPath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return false;

        var fullRootPath = Path.GetFullPath(rootPath);
        var relativePath = Path.GetRelativePath(fullRootPath, fullTargetPath);

        return relativePath != "." &&
               !relativePath.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relativePath);
    }
}
