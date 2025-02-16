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

namespace Wino.Services
{
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

            // Create attachments.

            foreach (var attachment in visitor.Attachments)
            {
                if (attachment.IsAttachment && attachment is MimePart attachmentPart)
                {
                    renderingModel.Attachments.Add(attachmentPart);
                }
            }

            if (message.Headers.Contains(HeaderId.ListUnsubscribe))
            {
                var unsubscribeLinks = message.Headers[HeaderId.ListUnsubscribe]
                    .Normalize()
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim([' ', '<', '>']));

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
}
