using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class CalendarIcsFileService : ICalendarIcsFileService
{
    private readonly INativeAppService _nativeAppService;
    private readonly ILogger _logger = Log.ForContext<CalendarIcsFileService>();

    public CalendarIcsFileService(INativeAppService nativeAppService)
    {
        _nativeAppService = nativeAppService;
    }

    public async Task SaveCalendarItemIcsAsync(Guid accountId, Guid calendarId, Guid calendarItemId, string remoteEventId, string remoteResourceHref, string eTag, string icsContent)
    {
        if (accountId == Guid.Empty || calendarId == Guid.Empty || calendarItemId == Guid.Empty || string.IsNullOrWhiteSpace(icsContent))
            return;

        var folderPath = await GetCalendarItemFolderPathAsync(accountId, calendarId, calendarItemId).ConfigureAwait(false);
        var icsPath = Path.Combine(folderPath, "event.ics");
        var metaPath = Path.Combine(folderPath, "event.meta.json");

        try
        {
            await File.WriteAllTextAsync(icsPath, icsContent).ConfigureAwait(false);

            var metadataContent = string.Join(
                Environment.NewLine,
                $"CalendarItemId={calendarItemId:N}",
                $"RemoteEventId={remoteEventId ?? string.Empty}",
                $"RemoteResourceHref={remoteResourceHref ?? string.Empty}",
                $"ETag={eTag ?? string.Empty}",
                $"UpdatedAtUtc={DateTime.UtcNow:O}");

            await File.WriteAllTextAsync(metaPath, metadataContent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save ICS file for account {AccountId} and calendar item {CalendarItemId}", accountId, calendarItemId);
        }
    }

    public async Task<string> GetCalendarItemIcsETagAsync(Guid accountId, Guid calendarId, Guid calendarItemId)
    {
        if (accountId == Guid.Empty || calendarId == Guid.Empty || calendarItemId == Guid.Empty)
            return string.Empty;

        try
        {
            var itemPath = await GetCalendarItemPathAsync(accountId, calendarId, calendarItemId).ConfigureAwait(false);
            var metaPath = Path.Combine(itemPath, "event.meta.json");

            if (!File.Exists(metaPath))
                return string.Empty;

            var lines = await File.ReadAllLinesAsync(metaPath).ConfigureAwait(false);

            foreach (var line in lines)
            {
                if (!line.StartsWith("ETag=", StringComparison.OrdinalIgnoreCase))
                    continue;

                return line["ETag=".Length..].Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load ICS metadata for account {AccountId}, calendar {CalendarId}, item {CalendarItemId}", accountId, calendarId, calendarItemId);
        }

        return string.Empty;
    }

    public async Task DeleteCalendarItemIcsAsync(Guid accountId, Guid calendarItemId)
    {
        if (accountId == Guid.Empty || calendarItemId == Guid.Empty)
            return;

        try
        {
            var accountRootPath = await GetAccountCalendarsRootPathAsync(accountId).ConfigureAwait(false);
            if (!Directory.Exists(accountRootPath))
                return;

            var calendarDirectories = Directory.GetDirectories(accountRootPath);

            foreach (var calendarDirectory in calendarDirectories)
            {
                var itemPath = Path.Combine(calendarDirectory, calendarItemId.ToString("N"));
                if (Directory.Exists(itemPath))
                {
                    Directory.Delete(itemPath, recursive: true);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete ICS folder for account {AccountId} and calendar item {CalendarItemId}", accountId, calendarItemId);
        }
    }

    public async Task DeleteCalendarIcsForCalendarAsync(Guid accountId, Guid calendarId)
    {
        if (accountId == Guid.Empty || calendarId == Guid.Empty)
            return;

        try
        {
            var calendarPath = await GetCalendarFolderPathAsync(accountId, calendarId).ConfigureAwait(false);
            if (Directory.Exists(calendarPath))
            {
                Directory.Delete(calendarPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete ICS folder for account {AccountId} and calendar {CalendarId}", accountId, calendarId);
        }
    }

    private async Task<string> GetCalendarItemFolderPathAsync(Guid accountId, Guid calendarId, Guid calendarItemId)
    {
        var calendarPath = await GetCalendarFolderPathAsync(accountId, calendarId).ConfigureAwait(false);
        var itemDirectory = Path.Combine(calendarPath, calendarItemId.ToString("N"));
        Directory.CreateDirectory(itemDirectory);
        return itemDirectory;
    }

    private async Task<string> GetCalendarItemPathAsync(Guid accountId, Guid calendarId, Guid calendarItemId)
    {
        var root = await GetIcsRootPathAsync().ConfigureAwait(false);
        return Path.Combine(
            root,
            accountId.ToString("N"),
            "calendars",
            calendarId.ToString("N"),
            calendarItemId.ToString("N"));
    }

    private async Task<string> GetCalendarFolderPathAsync(Guid accountId, Guid calendarId)
    {
        var accountRootPath = await GetAccountCalendarsRootPathAsync(accountId).ConfigureAwait(false);
        var calendarDirectory = Path.Combine(accountRootPath, calendarId.ToString("N"));
        Directory.CreateDirectory(calendarDirectory);
        return calendarDirectory;
    }

    private async Task<string> GetAccountCalendarsRootPathAsync(Guid accountId)
    {
        var root = await GetIcsRootPathAsync().ConfigureAwait(false);
        var accountPath = Path.Combine(root, accountId.ToString("N"), "calendars");
        Directory.CreateDirectory(accountPath);
        return accountPath;
    }

    private async Task<string> GetIcsRootPathAsync()
    {
        var mimeRootPath = await _nativeAppService.GetMimeMessageStoragePath().ConfigureAwait(false);
        var icsRootPath = Path.Combine(mimeRootPath, "CalendarIcs");
        Directory.CreateDirectory(icsRootPath);
        return icsRootPath;
    }
}
