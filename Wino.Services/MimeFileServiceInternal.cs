using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;
using Wino.Services.Extensions;

namespace Wino.Services;

/// <summary>
/// Companion-process implementation of the MIME file store: adds MimeMessage
/// parsing/writing and render-model creation on top of the shared MimeFileService.
/// </summary>
public class MimeFileServiceInternal : MimeFileService, IMimeFileServiceInternal
{
    private ILogger _logger = Log.ForContext<MimeFileServiceInternal>();

    public MimeFileServiceInternal(INativeAppService nativeAppService) : base(nativeAppService)
    {
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

    public HtmlPreviewVisitor CreateHTMLPreviewVisitor(MimeMessage message, string mimeLocalPath)
    {
        var visitor = new HtmlPreviewVisitor(mimeLocalPath);

        message.Accept(visitor);

        // TODO: Match cid with attachments if any.

        return visitor;
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

        var accessibleText = !string.IsNullOrWhiteSpace(message.TextBody)
            ? message.TextBody.Trim()
            : HtmlAgilityPackExtensions.GetAccessibleText(finalRenderHtml);

        // S/MIME state (signatures, encryption) is computed by the companion via
        // ISmimeService and attached to the model by the rendering view model.
        var renderingModel = new MailRenderModel(finalRenderHtml, options, accessibleText);

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
}
