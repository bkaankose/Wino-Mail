using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class MimeStorageService : IMimeStorageService
{
    private readonly INativeAppService _nativeAppService;
    private readonly IMailService _mailService;
    private readonly ILogger _logger = Log.ForContext<MimeStorageService>();

    public MimeStorageService(INativeAppService nativeAppService, IMailService mailService)
    {
        _nativeAppService = nativeAppService;
        _mailService = mailService;
    }

    public Task<string> GetMimeRootPathAsync() => _nativeAppService.GetMimeMessageStoragePath();

    public async Task<Dictionary<Guid, long>> GetAccountsMimeStorageSizesAsync(IEnumerable<Guid> accountIds)
    {
        var mimeRoot = await GetMimeRootPathAsync().ConfigureAwait(false);
        var result = new Dictionary<Guid, long>();

        foreach (var accountId in accountIds)
        {
            var accountPath = Path.Combine(mimeRoot, accountId.ToString());
            result[accountId] = GetDirectorySizeSafe(accountPath);
        }

        return result;
    }

    public async Task DeleteAccountMimeStorageAsync(Guid accountId)
    {
        var mimeRoot = await GetMimeRootPathAsync().ConfigureAwait(false);
        var accountPath = Path.Combine(mimeRoot, accountId.ToString());

        if (Directory.Exists(accountPath))
        {
            Directory.Delete(accountPath, true);
        }
    }

    public async Task<int> DeleteAccountMimeStorageOlderThanAsync(Guid accountId, DateTime cutoffDateUtc)
    {
        var mailCopies = await _mailService.GetMailCopiesBeforeDateAsync(accountId, cutoffDateUtc).ConfigureAwait(false);

        if (mailCopies.Count == 0)
            return 0;

        var mimeRoot = await GetMimeRootPathAsync().ConfigureAwait(false);
        var accountPath = Path.Combine(mimeRoot, accountId.ToString());
        var fileIds = mailCopies.Select(a => a.FileId).Distinct().ToList();
        int deletedFolderCount = 0;

        foreach (var fileId in fileIds)
        {
            var mimeDirectory = Path.Combine(accountPath, fileId.ToString());

            if (!Directory.Exists(mimeDirectory))
                continue;

            try
            {
                Directory.Delete(mimeDirectory, true);
                deletedFolderCount++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to delete MIME directory {DirectoryPath}", mimeDirectory);
            }
        }

        return deletedFolderCount;
    }

    private static long GetDirectorySizeSafe(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        long total = 0;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(filePath).Length;
                }
                catch
                {
                    // Ignore unreadable files and continue calculating.
                }
            }
        }
        catch
        {
            return 0;
        }

        return total;
    }
}
