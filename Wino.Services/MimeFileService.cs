using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using MimeKit;
using MimeKit.Cryptography;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;
using Wino.Services.Extensions;

namespace Wino.Services;

public class MimeFileService : IMimeFileService
{
    private readonly INativeAppService _nativeAppService;
    private ILogger _logger = Log.ForContext<MimeFileService>();

    public MimeFileService(INativeAppService nativeAppService)
    {
        _nativeAppService = nativeAppService;
    }

    public async Task<MimeMessageInformation> GetMimeMessageInformationAsync(Guid fileId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var resourcePath = await GetMimeResourcePathAsync(accountId, fileId).ConfigureAwait(false);
        var mimeFilePath = GetEMLPath(resourcePath);

        var loadedMimeMessage = await MimeMessage.LoadAsync(mimeFilePath, cancellationToken).ConfigureAwait(false);

        return new MimeMessageInformation(loadedMimeMessage, resourcePath);
    }

    public async Task<MimeMessageInformation> GetMimeMessageInformationAsync(byte[] fileBytes, string emlDirectoryPath, CancellationToken cancellationToken = default)
    {
        var memoryStream = new MemoryStream(fileBytes);

        var loadedMimeMessage = await MimeMessage.LoadAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        return new MimeMessageInformation(loadedMimeMessage, emlDirectoryPath);
    }

    public async Task<bool> SaveMimeMessageAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId)
    {
        try
        {
            var resourcePath = await GetMimeResourcePathAsync(accountId, fileId).ConfigureAwait(false);
            var completeFilePath = GetEMLPath(resourcePath);

            using var fileStream = File.Open(completeFilePath, FileMode.OpenOrCreate);

            await mimeMessage.WriteToAsync(fileStream).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not save mime file for FileId: {FileId}", fileId);
        }

        return false;
    }

    private string GetEMLPath(string resourcePath) => $"{resourcePath}\\mail.eml";

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

    public HtmlPreviewVisitor CreateHTMLPreviewVisitor(MimeMessage message, string mimeLocalPath)
    {
        var visitor = new HtmlPreviewVisitor(mimeLocalPath);

        message.Accept(visitor);

        // TODO: Match cid with attachments if any.

        return visitor;
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

    public MailRenderModel GetMailRenderModel(MimeMessage message, string mimeLocalPath, MailRenderingOptions options = null)
    {
        var visitor = CreateHTMLPreviewVisitor(message, mimeLocalPath);

        string finalRenderHtml = visitor.HtmlBody;

        // Check whether we need to purify the generated HTML from visitor.
        // No need to create HtmlDocument if not required.

        if (options != null && options.IsPurifyingNeeded())
        {
            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(visitor.HtmlBody);

            // Clear <img> src attribute.

            if (!options.LoadImages)
                document.ClearImages();

            if (!options.LoadStyles)
                document.ClearStyles();

            // Update final HTML.
            finalRenderHtml = document.DocumentNode.OuterHtml;
        }

        var renderingModel = new MailRenderModel(finalRenderHtml, options);

        renderingModel.Signatures = visitor.Signatures;

        // S/MIME encryption detection: if the body is ApplicationPkcs7Mime and SecureMimeType is EnvelopedData
        renderingModel.IsSmimeEncrypted = message.Body is ApplicationPkcs7Mime encrypted &&
            encrypted.SecureMimeType == SecureMimeType.EnvelopedData;

        // Create attachments.
        foreach (var attachment in visitor.Attachments)
        {
            if (attachment.IsAttachment && attachment is MimePart attachmentPart)
            {
                // Exclude S/MIME encryption/decryption certificates
                var contentType = attachmentPart.ContentType?.MimeType?.ToLowerInvariant();
                var fileName = attachmentPart.FileName?.ToLowerInvariant();
                if ((contentType == "application/pkcs7-signature"
                    || contentType == "application/x-pkcs7-signature"
                    && fileName == "smime.p7s") || (contentType == "application/pkcs7-mime"
                                                    || contentType == "application/x-pkcs7-mime"
                                                    && fileName == "smime.p7m"))
                    continue;
                renderingModel.Attachments.Add(attachmentPart);
            }
        }

        if (message.Headers.Contains(HeaderId.ListUnsubscribe))
        {
            var unsubscribeLinks = message.Headers[HeaderId.ListUnsubscribe]
                .Normalize()
                .Split([','], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().Trim(['<', '>']));

            // Only two types of unsubscribe links are possible.
            // So each has it's own property to simplify the usage.
            renderingModel.UnsubscribeInfo = new UnsubscribeInfo()
            {
                HttpLink = unsubscribeLinks.FirstOrDefault(x => x.StartsWith("http", StringComparison.OrdinalIgnoreCase)),
                MailToLink = unsubscribeLinks.FirstOrDefault(x => x.StartsWith("mailto", StringComparison.OrdinalIgnoreCase)),
                IsOneClick = message.Headers.Contains(HeaderId.ListUnsubscribePost)
            };
        }

        return renderingModel;
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
