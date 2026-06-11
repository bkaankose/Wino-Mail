using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// MimeKit-free part of the MIME file store: path resolution, existence checks and the
/// translated-html/summary caches. Shared with the UI process. Parsing/writing of the
/// MimeMessage itself lives in the companion-only MimeFileServiceInternal subclass.
/// </summary>
public class MimeFileService : IMimeFileService
{
    private readonly INativeAppService _nativeAppService;
    private ILogger _logger = Log.ForContext<MimeFileService>();

    public MimeFileService(INativeAppService nativeAppService)
    {
        _nativeAppService = nativeAppService;
    }

    protected static string GetEMLPath(string resourcePath) => $"{resourcePath}\\mail.eml";

    public async Task<string> GetMimeResourcePathAsync(Guid accountId, Guid fileId)
    {
        var mimeFolderPath = await _nativeAppService.GetMimeMessageStoragePath().ConfigureAwait(false);
        var mimeDirectory = Path.Combine(mimeFolderPath, accountId.ToString(), fileId.ToString());

        if (!Directory.Exists(mimeDirectory))
            Directory.CreateDirectory(mimeDirectory);

        return mimeDirectory;
    }

    public async Task<bool> IsMimeExistAsync(Guid accountId, Guid fileId)
    {
        var resourcePath = await GetMimeResourcePathAsync(accountId, fileId);
        var completeFilePath = GetEMLPath(resourcePath);

        return File.Exists(completeFilePath);
    }

    public async Task<bool> DeleteMimeMessageAsync(Guid accountId, Guid fileId)
    {
        var resourcePath = await GetMimeResourcePathAsync(accountId, fileId);
        var completeFilePath = GetEMLPath(resourcePath);

        if (File.Exists(completeFilePath))
        {
            try
            {
                File.Delete(completeFilePath);

                _logger.Information("Mime file deleted for {FileId}", fileId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Could not delete mime file for {FileId}", fileId);
            }

            return false;
        }

        return true;
    }

    public async Task<string> GetTranslatedHtmlAsync(Guid accountId, Guid fileId, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return null;
        }

        try
        {
            var translatedHtmlPath = await GetTranslatedHtmlPathAsync(accountId, fileId, targetLanguage).ConfigureAwait(false);
            if (!File.Exists(translatedHtmlPath))
            {
                return null;
            }

            return await File.ReadAllTextAsync(translatedHtmlPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not read translated html cache for FileId: {FileId}, Language: {Language}", fileId, targetLanguage);
            return null;
        }
    }

    public async Task SaveTranslatedHtmlAsync(Guid accountId, Guid fileId, string targetLanguage, string html, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage) || string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        try
        {
            var translatedHtmlPath = await GetTranslatedHtmlPathAsync(accountId, fileId, targetLanguage).ConfigureAwait(false);
            await File.WriteAllTextAsync(translatedHtmlPath, html, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not save translated html cache for FileId: {FileId}, Language: {Language}", fileId, targetLanguage);
        }
    }

    public async Task<string> GetSummaryTextAsync(Guid accountId, Guid fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var summaryPath = await GetSummaryTextPathAsync(accountId, fileId).ConfigureAwait(false);
            if (!File.Exists(summaryPath))
            {
                return null;
            }

            return await File.ReadAllTextAsync(summaryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not read summary cache for FileId: {FileId}", fileId);
            return null;
        }
    }

    public async Task SaveSummaryTextAsync(Guid accountId, Guid fileId, string summary, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        try
        {
            var summaryPath = await GetSummaryTextPathAsync(accountId, fileId).ConfigureAwait(false);
            await File.WriteAllTextAsync(summaryPath, NormalizeSummaryText(summary), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not save summary cache for FileId: {FileId}", fileId);
        }
    }

    public async Task DeleteUserMimeCacheAsync(Guid accountId)
    {
        var mimeFolderPath = await _nativeAppService.GetMimeMessageStoragePath().ConfigureAwait(false);
        var mimeDirectory = Path.Combine(mimeFolderPath, accountId.ToString());

        try
        {
            if (Directory.Exists(mimeDirectory))
            {
                Directory.Delete(mimeDirectory, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to remove user's mime cache folder.");
        }
    }

    private async Task<string> GetTranslatedHtmlPathAsync(Guid accountId, Guid fileId, string targetLanguage)
    {
        var resourcePath = await GetMimeResourcePathAsync(accountId, fileId).ConfigureAwait(false);
        return Path.Combine(resourcePath, $"translated-{SanitizeFileNamePart(targetLanguage)}.html");
    }

    private async Task<string> GetSummaryTextPathAsync(Guid accountId, Guid fileId)
    {
        var resourcePath = await GetMimeResourcePathAsync(accountId, fileId).ConfigureAwait(false);
        return Path.Combine(resourcePath, "summary.txt");
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitizedChars = value
            .Trim()
            .Select(ch => invalidCharacters.Contains(ch) ? '_' : char.ToLowerInvariant(ch))
            .ToArray();

        return sanitizedChars.Length == 0 ? "default" : new string(sanitizedChars);
    }

    private static string NormalizeSummaryText(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        if (!summary.Contains('<'))
        {
            return summary.Trim();
        }

        var document = new HtmlDocument();
        document.LoadHtml(summary);

        var lineBreakNodes = document.DocumentNode.SelectNodes("//br|//p|//div|//li");
        if (lineBreakNodes != null)
        {
            foreach (var node in lineBreakNodes)
            {
                if (node.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
                {
                    node.ParentNode?.InsertBefore(document.CreateTextNode(Environment.NewLine + "- "), node);
                }
                else
                {
                    node.ParentNode?.InsertBefore(document.CreateTextNode(Environment.NewLine), node);
                }
            }
        }

        var plainText = HtmlEntity.DeEntitize(document.DocumentNode.InnerText ?? string.Empty);
        return string.Join(
            Environment.NewLine,
            plainText
                .Split([Environment.NewLine], StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }
}
